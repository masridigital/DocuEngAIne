using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>meraki_get_organization_networks</c> JSON (vendor passthrough, often
/// JSON-RPC wrapped) to <see cref="ExternalNetworkDto"/>. Live list objects use <c>id</c> (string),
/// <c>name</c>, <c>organizationId</c>, optional <c>productTypes</c> and <c>tags</c> (string arrays).
/// A JSON array is the list. Skip rows missing <c>id</c> or <c>name</c>.
/// <c>organizationId</c> is required on the tool call. Compact schema <c>perPage</c> is 3–100000
/// (API default 1000); this mapper clamps 3–1000. Cursor is <c>startingAfter</c> = last network
/// id from the previous page. Unwrap matches <see cref="MerakiOrganizationMapper"/>.
/// </summary>
public static class MerakiNetworkMapper
{
    public const string ToolName = "meraki_get_organization_networks";
    public const int DefaultPageSize = 1000;
    public const int MinPageSize = 3;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<ExternalNetworkDto> MapNetworks(string mcpBody)
        => MapNetworks(mcpBody, organizationId: null, out _, out _);

    public static IReadOnlyList<ExternalNetworkDto> MapNetworks(string mcpBody, out string? lastNetworkId)
        => MapNetworks(mcpBody, organizationId: null, out lastNetworkId, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count, or one unmappable row ends the pull and the run still reports Succeeded.
    /// <paramref name="organizationId"/> fills <c>OrganizationExternalId</c> when a row omits it.
    /// </summary>
    public static IReadOnlyList<ExternalNetworkDto> MapNetworks(string mcpBody, out string? lastNetworkId, out int rowCount)
        => MapNetworks(mcpBody, organizationId: null, out lastNetworkId, out rowCount);

    public static IReadOnlyList<ExternalNetworkDto> MapNetworks(
        string mcpBody,
        string? organizationId,
        out string? lastNetworkId,
        out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var networks = new List<ExternalNetworkDto>();
        lastNetworkId = null;
        rowCount = 0;
        foreach (var network in EnumerateNetworks(payload))
        {
            rowCount++;
            var id = ReadString(network, "id");
            if (!string.IsNullOrWhiteSpace(id))
                lastNetworkId = id;
            var mapped = MapNetwork(network, organizationId);
            if (mapped is not null)
                networks.Add(mapped);
        }
        return networks;
    }

    public static string BuildArgumentsJson(string organizationId, string? startingAfter, int pageSize = DefaultPageSize)
    {
        if (string.IsNullOrWhiteSpace(organizationId))
            throw new ArgumentException("organizationId is required.", nameof(organizationId));

        var size = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["organizationId"] = organizationId,
            ["perPage"] = size,
        };
        if (!string.IsNullOrWhiteSpace(startingAfter))
            args["startingAfter"] = startingAfter;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalNetworkDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        string organizationId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(organizationId))
            throw new ArgumentException("organizationId is required.", nameof(organizationId));

        var size = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        var networks = new List<ExternalNetworkDto>();
        string? startingAfter = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(organizationId, startingAfter, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapNetworks(body, organizationId, out var lastId, out var rowCount);
            networks.AddRange(mapped);
            // Raw rows, never mapped rows -- see MerakiOrganizationMapper for the same hazard.
            if (rowCount == 0 || rowCount < size)
                break;
            if (string.IsNullOrWhiteSpace(lastId))
                break;
            startingAfter = lastId;
        }
        return networks;
    }

    private static ExternalNetworkDto? MapNetwork(JsonElement network, string? fallbackOrganizationId)
    {
        if (network.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(network, "id");
        var name = ReadString(network, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        var organizationId = ReadString(network, "organizationId") ?? fallbackOrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
            return null;

        return new ExternalNetworkDto(
            ExternalId: id,
            OrganizationExternalId: organizationId,
            Name: name.Trim(),
            ProductTypes: ReadStringList(network, "productTypes"),
            Tags: ReadStringList(network, "tags"));
    }

    private static IReadOnlyList<string>? ReadStringList(JsonElement obj, string name)
    {
        if (!TryGetProperty(obj, out var value, name) || value.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var text = item.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(item.GetString()) ? null : item.GetString(),
                JsonValueKind.Number => item.GetRawText(),
                _ => null,
            };
            if (text is not null)
                items.Add(text);
        }

        return items;
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }

    private static bool TryGetProperty(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateNetworks(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        return [];
    }

    private static JsonElement UnwrapMcpPayload(string mcpBody)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(mcpBody);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Meraki MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Meraki MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Meraki MCP tool error: {errText ?? payload.GetRawText()}");
        }

        var text = ReadContentText(payload);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try
                {
                    return JsonSerializer.Deserialize<JsonElement>(text);
                }
                catch (JsonException)
                {
                    // fall through to structured payload
                }
            }
        }

        return payload;
    }

    private static string? ReadContentText(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryGetProperty(payload, out var content, "content") || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && TryGetProperty(item, out var text, "text")
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }

        return null;
    }
}

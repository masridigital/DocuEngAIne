using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>ninja_list_locations</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to location DTOs. The list is a JSON array; objects use <c>id</c>, <c>name</c>, <c>address</c>,
/// <c>description</c>, and <c>organizationId</c>. <c>organizationId</c> is the provider client id
/// bound as <see cref="ExternalLocationDto.ClientExternalId"/>. The list has no city or inactive
/// flag; do not invent either. <c>description</c> has no DTO field — do not dump it into
/// <c>Address</c>. List is the sync source — do not call <c>ninja_get_organization_locations</c>.
/// Cursor is <c>after</c> = last location id from the previous page, exactly like
/// <see cref="NinjaOrganizationMapper"/>.
/// </summary>
public static class NinjaLocationMapper
{
    public const string ToolName = "ninja_list_locations";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<ExternalLocationDto> MapLocations(string mcpBody)
        => MapLocations(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalLocationDto> MapLocations(string mcpBody, out int? lastLocationId)
        => MapLocations(mcpBody, out lastLocationId, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count, or one unmappable row ends the pull and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalLocationDto> MapLocations(string mcpBody, out int? lastLocationId, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var locations = new List<ExternalLocationDto>();
        lastLocationId = null;
        rowCount = 0;
        foreach (var location in EnumerateLocations(payload))
        {
            rowCount++;
            if (TryReadId(location, out var id))
                lastLocationId = id;
            var mapped = MapLocation(location);
            if (mapped is not null)
                locations.Add(mapped);
        }
        return locations;
    }

    public static string BuildArgumentsJson(int? afterLocationId, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["pageSize"] = size,
        };
        if (afterLocationId is int after)
            args["after"] = after;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalLocationDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var locations = new List<ExternalLocationDto>();
        int? after = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(after, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapLocations(body, out var lastId, out var rowCount);
            locations.AddRange(mapped);
            // Raw rows, never mapped rows: a location with no name is dropped, and testing the
            // mapped count would read that short page as the last one and abandon the rest.
            if (rowCount == 0 || rowCount < size)
                break;
            if (lastId is null)
                break;
            after = lastId;
        }
        return locations;
    }

    private static ExternalLocationDto? MapLocation(JsonElement location)
    {
        if (location.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(location, "id");
        var name = ReadString(location, "name");
        var organizationId = ReadString(location, "organizationId");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(organizationId))
            return null;

        return new ExternalLocationDto(
            ExternalId: id,
            ClientExternalId: organizationId,
            Name: name.Trim(),
            Address: ReadString(location, "address"));
    }

    private static bool TryReadId(JsonElement location, out int id)
    {
        id = 0;
        if (location.ValueKind != JsonValueKind.Object || !TryGetProperty(location, out var value, "id"))
            return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out id))
            return true;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out id);
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

    private static IEnumerable<JsonElement> EnumerateLocations(JsonElement payload)
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
            throw new InvalidOperationException("NinjaOne MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"NinjaOne MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"NinjaOne MCP tool error: {errText ?? payload.GetRawText()}");
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

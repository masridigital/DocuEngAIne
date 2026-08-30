using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>action1_list_endpoints</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to device DTOs. Live list is a ResultPage whose rows live in <c>items</c> (optional <c>next</c> /
/// <c>next_page</c> is ignored for paging). Each endpoint uses <c>id</c> (GUID string) and
/// <c>hostname</c> (Action1 also emits the computer name as <c>name</c>). <c>OS</c> is the type/notes
/// value and lands on <see cref="ExternalDeviceDto.NodeClass"/>.
/// <c>orgId</c> is required on the tool (GUID from <c>action1_list_organizations</c>) and becomes
/// <see cref="ExternalDeviceDto.OrganizationExternalId"/> — do not invent it from other keys.
/// Skip rows missing <c>id</c> or a hostname. Optional vendor <c>status</c> Active/Inactive is
/// mapped through, not used as a pull filter; <c>Inactive</c> is skip-inactive later in sync.
/// Compact schema <c>pageSize</c> defaults to 50 (max 100). Cursor is <c>from</c> = 0-based offset.
/// Page on the raw <c>items</c> count, never the mapped count. Stop when the page is empty or
/// <c>items.Length &lt; pageSize</c>. List is the sync source — do not call <c>action1_get_endpoint</c>.
/// </summary>
public static class Action1EndpointMapper
{
    public const string ToolName = "action1_list_endpoints";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static IReadOnlyList<ExternalDeviceDto> MapEndpoints(string mcpBody, string orgId)
        => MapEndpoints(mcpBody, orgId, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing an id or hostname are dropped. Paging must turn on the
    /// raw count, or one unmappable row ends the pull and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalDeviceDto> MapEndpoints(string mcpBody, string orgId, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var devices = new List<ExternalDeviceDto>();
        rowCount = 0;
        foreach (var endpoint in EnumerateEndpoints(payload))
        {
            rowCount++;
            var mapped = MapEndpoint(endpoint, orgId);
            if (mapped is not null)
                devices.Add(mapped);
        }
        return devices;
    }

    public static string BuildArgumentsJson(string orgId, int? from, int pageSize = DefaultPageSize)
    {
        if (string.IsNullOrWhiteSpace(orgId))
            throw new ArgumentException("orgId is required.", nameof(orgId));

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["orgId"] = orgId,
            ["pageSize"] = size,
        };
        // Compact: omit from on the first page (offset 0). Do not send status — Inactive is
        // skip-inactive later, not a pull filter.
        if (from is int cursor && cursor > 0)
            args["from"] = cursor;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalDeviceDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        string orgId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orgId))
            throw new ArgumentException("orgId is required.", nameof(orgId));

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var devices = new List<ExternalDeviceDto>();
        var from = 0;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(orgId, from == 0 ? null : from, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapEndpoints(body, orgId, out var rowCount);
            devices.AddRange(mapped);
            // Raw items, never mapped rows — a single endpoint with no hostname is dropped, and
            // testing mapped.Count here would read that short page as the last one and silently
            // abandon every remaining endpoint.
            if (rowCount == 0 || rowCount < size)
                break;
            from += rowCount;
        }
        return devices;
    }

    private static ExternalDeviceDto? MapEndpoint(JsonElement endpoint, string orgId)
    {
        if (endpoint.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(endpoint, "id");
        var hostname = ReadString(endpoint, "hostname") ?? ReadString(endpoint, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(hostname))
            return null;

        if (string.IsNullOrWhiteSpace(orgId))
            return null;

        // status Inactive is not filtered here. SkipInactive is honoured in sync later.

        return new ExternalDeviceDto(
            ExternalId: id,
            OrganizationExternalId: orgId,
            Name: hostname.Trim(),
            NodeClass: ReadString(endpoint, "OS", "os"));
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
        if (obj.ValueKind == JsonValueKind.Object)
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
        }

        value = default;
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateEndpoints(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var items, "items")
            && items.ValueKind == JsonValueKind.Array)
        {
            return items.EnumerateArray();
        }

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
            throw new InvalidOperationException("Action1 MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Action1 MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Action1 MCP tool error: {errText ?? payload.GetRawText()}");
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

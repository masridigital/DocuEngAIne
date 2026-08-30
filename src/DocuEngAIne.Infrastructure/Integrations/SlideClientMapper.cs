using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>slide_list_clients</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Compact returns the raw Slide envelope
/// <c>{ "data": [ { client_id, name, comments }, ... ], "pagination": { next_offset? } }</c>.
/// Map <c>client_id</c> → <c>ExternalId</c> and <c>name</c> → <c>Name</c>. Skip rows missing
/// either. <c>comments</c> has no matching <see cref="ExternalCompanyDto"/> property — ignore it.
/// There is no inactive flag; do not invent <c>IsInactive</c>. Do not call <c>slide_get_client</c>.
/// Compact schema <c>offset</c> is zero-based (default 0); <c>limit</c> defaults to 50 (max 50).
/// Cursor is <c>pagination.next_offset</c>. Page until <c>data</c> is empty or <c>next_offset</c>
/// is absent. Page on raw <c>data</c> length, never mapped count.
/// </summary>
public static class SlideClientMapper
{
    public const string ToolName = "slide_list_clients";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 50;

    public static IReadOnlyList<ExternalCompanyDto> MapClients(string mcpBody)
        => MapClients(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapClients(string mcpBody, out int? nextOffset)
        => MapClients(mcpBody, out nextOffset, out _);

    /// <summary>
    /// Maps one page. <paramref name="dataCount"/> is the number of rows Slide returned, which is
    /// NOT the number mapped — rows missing <c>client_id</c> or <c>name</c> are dropped. Paging must
    /// turn on the raw count and <c>pagination.next_offset</c>, or one unmappable row ends the pull
    /// and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapClients(string mcpBody, out int? nextOffset, out int dataCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        nextOffset = ReadNextOffset(payload);
        var companies = new List<ExternalCompanyDto>();
        dataCount = 0;
        foreach (var client in EnumerateClients(payload))
        {
            dataCount++;
            var mapped = MapClient(client);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
    }

    public static string BuildArgumentsJson(int? offset, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["limit"] = size,
        };
        if (offset is int o && o > 0)
            args["offset"] = o;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var companies = new List<ExternalCompanyDto>();
        int? offset = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(offset, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapClients(body, out var nextOffset, out var dataCount);
            companies.AddRange(mapped);
            // Raw data length + next_offset, never mapped rows — see NinjaOrganizationMapper.
            // Empty data or a missing / non-advancing next_offset ends the pull; a short mapped page is not.
            if (dataCount == 0)
                break;
            if (nextOffset is not int next || next <= (offset ?? 0))
                break;
            offset = next;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapClient(JsonElement client)
    {
        if (client.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(client, "client_id");
        var name = ReadString(client, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim());
    }

    /// <summary>
    /// Compact/Slide last page omits <c>pagination.next_offset</c>. Missing / null / empty string
    /// all mean stop. A present integer (including string-encoded) is the next <c>offset</c>.
    /// </summary>
    private static int? ReadNextOffset(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetProperty(payload, out var pagination, "pagination")
            || pagination.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryGetProperty(pagination, out var next, "next_offset"))
            return null;

        if (next.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (next.ValueKind == JsonValueKind.Number && next.TryGetInt32(out var n))
            return n;
        if (next.ValueKind == JsonValueKind.String)
        {
            var s = next.GetString();
            if (string.IsNullOrWhiteSpace(s))
                return null;
            if (int.TryParse(s, out var parsed))
                return parsed;
        }

        return null;
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

    private static IEnumerable<JsonElement> EnumerateClients(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var data, "data")
            && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray();
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
            throw new InvalidOperationException("Slide MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Slide MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Slide MCP tool error: {errText ?? payload.GetRawText()}");
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

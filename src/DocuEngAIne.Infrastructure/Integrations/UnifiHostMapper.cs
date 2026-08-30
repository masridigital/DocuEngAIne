using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>unifi_sm_list_hosts</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Live list is a wrapper <c>{ "data": [ host, ... ], nextToken? }</c> plus
/// <c>httpStatusCode</c>/<c>traceId</c>. Each host uses <c>id</c>, <c>isBlocked</c>, and
/// <c>reportedState.name</c> (fallback <c>reportedState.hostname</c>) with optional
/// <c>reportedState.location.text</c> as city.
/// Do not call <c>unifi_sm_list_sites</c> — every site is <c>meta.name="default"</c>.
/// Do not ingest the huge <c>reportedState</c> blob (firmware, prefetch, WAN IPs, emails).
/// <c>owner</c> is a relay flag, not inactive — do not skip <c>owner:false</c>.
/// Compact <c>pageSize</c> caps at 50. Cursor is wrapper <c>nextToken</c>.
/// </summary>
public static class UnifiHostMapper
{
    public const string ToolName = "unifi_sm_list_hosts";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 50;

    public static IReadOnlyList<ExternalCompanyDto> MapHosts(string mcpBody)
        => MapHosts(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapHosts(string mcpBody, out string? nextToken)
        => MapHosts(mcpBody, out nextToken, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapHosts(string mcpBody, out string? nextToken, out int dataCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        nextToken = ReadNextToken(payload);
        var companies = new List<ExternalCompanyDto>();
        dataCount = 0;
        foreach (var host in EnumerateHosts(payload))
        {
            dataCount++;
            var mapped = MapHost(host);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
    }

    public static string BuildArgumentsJson(string? nextToken, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["pageSize"] = size,
        };
        if (!string.IsNullOrWhiteSpace(nextToken))
            args["nextToken"] = nextToken;
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
        string? nextToken = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(nextToken, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapHosts(body, out var pageNextToken, out var dataCount);
            companies.AddRange(mapped);
            if (dataCount == 0 || dataCount < size)
                break;
            if (string.IsNullOrWhiteSpace(pageNextToken))
                break;
            nextToken = pageNextToken;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapHost(JsonElement host)
    {
        if (host.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(host, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var name = ReadReportedName(host);
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            City: ReadReportedCity(host),
            IsInactive: ReadIsBlocked(host));
    }

    private static string? ReadReportedName(JsonElement host)
    {
        if (!TryGetReportedState(host, out var reported))
            return null;
        return ReadString(reported, "name") ?? ReadString(reported, "hostname");
    }

    private static string? ReadReportedCity(JsonElement host)
    {
        if (!TryGetReportedState(host, out var reported))
            return null;
        if (!TryGetProperty(reported, out var location, "location") || location.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(location, "text");
    }

    private static bool TryGetReportedState(JsonElement host, out JsonElement reported)
    {
        if (TryGetProperty(host, out reported, "reportedState") && reported.ValueKind == JsonValueKind.Object)
            return true;
        reported = default;
        return false;
    }

    private static bool? ReadIsBlocked(JsonElement host)
    {
        if (!TryGetProperty(host, out var blocked, "isBlocked"))
            return null;

        if (blocked.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return blocked.GetBoolean();
        if (blocked.ValueKind == JsonValueKind.Number && blocked.TryGetInt32(out var n))
            return n != 0;
        if (blocked.ValueKind == JsonValueKind.String)
        {
            var s = blocked.GetString();
            if (bool.TryParse(s, out var b))
                return b;
        }

        return null;
    }

    private static string? ReadNextToken(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(payload, "nextToken");
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

    private static IEnumerable<JsonElement> EnumerateHosts(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var data, "data")
            && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray();

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
            throw new InvalidOperationException("UniFi MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"UniFi MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"UniFi MCP tool error: {errText ?? payload.GetRawText()}");
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

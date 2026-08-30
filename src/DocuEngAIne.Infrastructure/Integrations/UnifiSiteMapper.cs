using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>unifi_sm_list_sites</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to site DTOs. This is the ACCOUNT-WIDE Site Manager list — not
/// <c>unifi_net_list_sites</c> (per-console Network sites). Live list is a wrapper
/// <c>{ "data": [ site, ... ], nextToken? }</c> plus <c>httpStatusCode</c>/<c>traceId</c>.
/// Each site uses <c>siteId</c>, <c>hostId</c> (join to <c>unifi_sm_list_hosts</c>),
/// <c>meta.name</c> (fallback <c>"default"</c>), and <c>meta.timezone</c>.
/// UniFi Network sites are often named <c>default</c> — still map them.
/// Do not ingest the huge <c>statistics</c> block (client counts, ISP/WAN state).
/// Skip rows missing <c>siteId</c>. Compact <c>pageSize</c> caps at 200.
/// Cursor is wrapper <c>nextToken</c>.
/// </summary>
public static class UnifiSiteMapper
{
    public const string ToolName = "unifi_sm_list_sites";
    public const int DefaultPageSize = 200;
    public const int MaxPageSize = 200;

    public static IReadOnlyList<ExternalUnifiSiteDto> MapSites(string mcpBody)
        => MapSites(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalUnifiSiteDto> MapSites(string mcpBody, out string? nextToken)
        => MapSites(mcpBody, out nextToken, out _);

    /// <summary>
    /// Maps one page. <paramref name="dataCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing <c>siteId</c> are dropped. Paging must turn on the raw
    /// count, or one unmappable row ends the pull.
    /// </summary>
    public static IReadOnlyList<ExternalUnifiSiteDto> MapSites(string mcpBody, out string? nextToken, out int dataCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        nextToken = ReadNextToken(payload);
        var sites = new List<ExternalUnifiSiteDto>();
        dataCount = 0;
        foreach (var site in EnumerateSites(payload))
        {
            dataCount++;
            var mapped = MapSite(site);
            if (mapped is not null)
                sites.Add(mapped);
        }
        return sites;
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

    public static async Task<IReadOnlyList<ExternalUnifiSiteDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var sites = new List<ExternalUnifiSiteDto>();
        string? nextToken = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(nextToken, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapSites(body, out var pageNextToken, out var dataCount);
            sites.AddRange(mapped);
            if (dataCount == 0 || dataCount < size)
                break;
            if (string.IsNullOrWhiteSpace(pageNextToken))
                break;
            nextToken = pageNextToken;
        }
        return sites;
    }

    private static ExternalUnifiSiteDto? MapSite(JsonElement site)
    {
        if (site.ValueKind != JsonValueKind.Object)
            return null;

        var siteId = ReadString(site, "siteId");
        if (string.IsNullOrWhiteSpace(siteId))
            return null;

        return new ExternalUnifiSiteDto(
            ExternalId: siteId,
            HostExternalId: ReadString(site, "hostId") ?? "",
            Name: ReadMetaName(site),
            Timezone: ReadMetaTimezone(site));
    }

    /// <summary><c>meta.name</c> when present; otherwise the UniFi Network default site name.</summary>
    private static string ReadMetaName(JsonElement site)
    {
        if (!TryGetMeta(site, out var meta))
            return "default";
        return ReadString(meta, "name") ?? "default";
    }

    private static string? ReadMetaTimezone(JsonElement site)
    {
        if (!TryGetMeta(site, out var meta))
            return null;
        return ReadString(meta, "timezone");
    }

    private static bool TryGetMeta(JsonElement site, out JsonElement meta)
    {
        if (TryGetProperty(site, out meta, "meta") && meta.ValueKind == JsonValueKind.Object)
            return true;
        meta = default;
        return false;
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

    private static IEnumerable<JsonElement> EnumerateSites(JsonElement payload)
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

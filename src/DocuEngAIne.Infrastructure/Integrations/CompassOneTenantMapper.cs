using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>compassone_list_tenants</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Live list is <c>{ "data": [ tenant, ... ], "meta": { currentPage, totalItems,
/// pageSize, totalPages } }</c>. Each tenant uses <c>id</c> (UUID string), <c>name</c>, and optional
/// <c>domain</c> (bare host or URL → Website). There is no inactive flag; do not invent
/// <c>IsInactive</c> from <c>type</c>. Ignore <c>snapAgentUrl</c>, <c>contactGroupId</c>,
/// <c>accountId</c>, <c>vendorRecordId</c>, and <c>enableDeliveryEmail</c>. Compact schema
/// <c>page</c> is 1-based; <c>pageSize</c> defaults to 50 (max 1000). Page on raw <c>data</c>
/// length / <c>meta.pageSize</c>, never mapped count. Next page is <c>currentPage + 1</c> while
/// <c>currentPage &lt; totalPages</c>.
/// </summary>
public static class CompassOneTenantMapper
{
    public const string ToolName = "compassone_list_tenants";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<ExternalCompanyDto> MapTenants(string mcpBody)
        => MapTenants(mcpBody, out _, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapTenants(
        string mcpBody,
        out int? currentPage,
        out int? totalPages,
        out int rowCount)
        => MapTenants(mcpBody, out currentPage, out totalPages, out rowCount, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count and <c>meta.totalPages</c>, or one unmappable row ends the pull and the run still reports
    /// Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapTenants(
        string mcpBody,
        out int? currentPage,
        out int? totalPages,
        out int rowCount,
        out int? metaPageSize)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        ReadMeta(payload, out currentPage, out totalPages, out metaPageSize);
        var companies = new List<ExternalCompanyDto>();
        rowCount = 0;
        foreach (var tenant in EnumerateTenants(payload))
        {
            rowCount++;
            var mapped = MapTenant(tenant);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
    }

    public static string BuildArgumentsJson(int page, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["page"] = page < 1 ? 1 : page,
            ["pageSize"] = size,
        });
    }

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var companies = new List<ExternalCompanyDto>();
        var page = 1;
        const int maxPages = 500;
        for (var i = 1; i <= maxPages; i++)
        {
            var args = BuildArgumentsJson(page, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapTenants(body, out var currentPage, out var totalPages, out var rowCount, out var metaPageSize);
            companies.AddRange(mapped);
            // Raw data length / meta.pageSize, never mapped rows — see NinjaOrganizationMapper.
            // Empty data, a short page, or currentPage >= totalPages ends the pull.
            if (rowCount == 0)
                break;
            var pageNow = currentPage ?? page;
            var pages = totalPages ?? pageNow;
            if (pageNow >= pages)
                break;
            var sizeForShort = metaPageSize ?? size;
            if (rowCount < sizeForShort)
                break;
            page = pageNow + 1;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapTenant(JsonElement tenant)
    {
        if (tenant.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(tenant, "id");
        var name = ReadString(tenant, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            Website: ReadString(tenant, "domain"));
    }

    private static void ReadMeta(
        JsonElement payload,
        out int? currentPage,
        out int? totalPages,
        out int? pageSize)
    {
        currentPage = null;
        totalPages = null;
        pageSize = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetProperty(payload, out var meta, "meta")
            || meta.ValueKind != JsonValueKind.Object)
            return;

        currentPage = ReadInt(meta, "currentPage");
        totalPages = ReadInt(meta, "totalPages");
        pageSize = ReadInt(meta, "pageSize");
    }

    private static int? ReadInt(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;
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

    private static IEnumerable<JsonElement> EnumerateTenants(JsonElement payload)
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
            throw new InvalidOperationException("CompassOne MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"CompassOne MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"CompassOne MCP tool error: {errText ?? payload.GetRawText()}");
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

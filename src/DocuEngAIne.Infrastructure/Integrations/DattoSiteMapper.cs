using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>datto_list_sites</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Compact lists sites as <c>[{uid, name}]</c> or a catalog-equivalent
/// <c>{ "sites": [ ... ] }</c> wrapper. Each site uses <c>uid</c> (string) and <c>name</c>.
/// Skip rows missing <c>uid</c> or <c>name</c>. Catalog also mentions device counts and status;
/// there is no matching DTO field and no inactive flag in the Compact input schema — do not
/// invent <c>IsInactive</c>. Compact schema <c>pageNo</c> is 1-based (default 1);
/// <c>pageSize</c> defaults to 50 (max 250). Omit <c>siteName</c> to list every site.
/// Page on raw site count, never mapped count.
/// </summary>
public static class DattoSiteMapper
{
    public const string ToolName = "datto_list_sites";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 250;

    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(string mcpBody)
        => MapCompanies(mcpBody, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count, or one unmappable row ends the pull and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(string mcpBody, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var companies = new List<ExternalCompanyDto>();
        rowCount = 0;
        foreach (var site in EnumerateSites(payload))
        {
            rowCount++;
            var mapped = MapSite(site);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
    }

    public static string BuildArgumentsJson(int pageNo, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["pageNo"] = pageNo < 1 ? 1 : pageNo,
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
        var pageNo = 1;
        const int maxPages = 500;
        for (var i = 1; i <= maxPages; i++)
        {
            var args = BuildArgumentsJson(pageNo, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapCompanies(body, out var rowCount);
            companies.AddRange(mapped);
            // Raw site count, never mapped rows — see NinjaOrganizationMapper for the same hazard.
            if (rowCount == 0 || rowCount < size)
                break;
            pageNo++;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapSite(JsonElement site)
    {
        if (site.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(site, "uid");
        var name = ReadString(site, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim());
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

    private static IEnumerable<JsonElement> EnumerateSites(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var sites, "sites")
            && sites.ValueKind == JsonValueKind.Array)
            return sites.EnumerateArray();

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
            throw new InvalidOperationException("Datto RMM MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Datto RMM MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Datto RMM MCP tool error: {errText ?? payload.GetRawText()}");
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

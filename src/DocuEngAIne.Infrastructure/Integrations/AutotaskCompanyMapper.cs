using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>at_list_companies</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Live list is <c>{ "items": [ company, ... ], "pageDetails": { count, requestCount,
/// nextPageUrl } }</c>. Each company uses <c>id</c> (number, including 0), <c>companyName</c>,
/// <c>isActive</c>, optional <c>webAddress</c>, <c>city</c>, <c>state</c>, <c>address1</c>, and
/// <c>companyNumber</c> (slug when non-empty).
/// Do not call <c>at_list_active_companies</c> or <c>at_list_customer_companies</c> — those pre-filter;
/// <c>SkipInactive</c> is honoured in sync. Do not filter by <c>companyType</c>. Ignore
/// <c>userDefinedFields</c>, <c>billing*</c>, <c>taxID</c>, <c>fax</c>, <c>classification</c>,
/// <c>ownerResourceID</c>, <c>phone</c> (DTO has no Phone), and <c>parentCompanyID</c>.
/// Compact schema <c>maxRecords</c> defaults to 500 (API max 500); sync caps at 50. Cursor is
/// <c>nextPageUrl</c> from the previous <c>pageDetails.nextPageUrl</c>, passed verbatim. When
/// <c>nextPageUrl</c> is supplied, <c>maxRecords</c> is ignored. Page on raw <c>items</c> length
/// (<c>pageDetails.count</c> / <c>items.Length</c>), never mapped count. Stop when items are empty,
/// <c>count &lt; requestCount</c>, or <c>nextPageUrl</c> is null/empty.
/// </summary>
public static class AutotaskCompanyMapper
{
    public const string ToolName = "at_list_companies";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(string mcpBody)
        => MapCompanies(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(string mcpBody, out string? nextPageUrl)
        => MapCompanies(mcpBody, out nextPageUrl, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count and <c>pageDetails</c>, or one unmappable row ends the pull and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(string mcpBody, out string? nextPageUrl, out int rowCount)
        => MapCompanies(mcpBody, out nextPageUrl, out rowCount, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(
        string mcpBody,
        out string? nextPageUrl,
        out int rowCount,
        out bool shortPage)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var companies = new List<ExternalCompanyDto>();
        ReadPageDetails(payload, out nextPageUrl, out var count, out var requestCount);
        rowCount = 0;
        foreach (var company in EnumerateCompanies(payload))
        {
            rowCount++;
            var mapped = MapCompany(company);
            if (mapped is not null)
                companies.Add(mapped);
        }

        var rawCount = count ?? rowCount;
        shortPage = requestCount is int requested && rawCount < requested;
        return companies;
    }

    public static string BuildArgumentsJson(string? nextPageUrl, int pageSize = DefaultPageSize)
    {
        // Schema: when nextPageUrl is supplied, maxRecords is ignored. Omit it so the cursor is the
        // only argument and the previous page's URL is passed verbatim.
        if (!string.IsNullOrWhiteSpace(nextPageUrl))
            return JsonSerializer.Serialize(new Dictionary<string, object?> { ["nextPageUrl"] = nextPageUrl });

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["maxRecords"] = size });
    }

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var companies = new List<ExternalCompanyDto>();
        string? nextPageUrl = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(nextPageUrl, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapCompanies(body, out var pageNextUrl, out var rowCount, out var shortPage);
            companies.AddRange(mapped);
            // Raw items length / pageDetails.count, never mapped rows — see NinjaOrganizationMapper.
            // Empty items, a short page (count < requestCount), or a null/empty nextPageUrl ends the pull.
            if (rowCount == 0 || shortPage)
                break;
            if (string.IsNullOrWhiteSpace(pageNextUrl))
                break;
            nextPageUrl = pageNextUrl;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapCompany(JsonElement company)
    {
        if (company.ValueKind != JsonValueKind.Object)
            return null;

        // id 0 is a real Autotask company (Pacific Cloud Cyber). Do not treat 0 as missing.
        var id = ReadId(company);
        var name = ReadString(company, "companyName");
        if (id is null || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            Slug: ReadNonEmptyString(company, "companyNumber"),
            City: ReadString(company, "city"),
            State: ReadString(company, "state"),
            Website: ReadString(company, "webAddress"),
            Address: ReadString(company, "address1"),
            IsInactive: ReadIsInactive(company));
    }

    /// <summary>
    /// Autotask company ids are numbers, including 0. Missing / null / non-numeric id is unmappable.
    /// </summary>
    private static string? ReadId(JsonElement company)
    {
        if (!TryGetProperty(company, out var value, "id"))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
            return value.GetRawText();

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        return null;
    }

    /// <summary><c>isActive</c> false → <c>IsInactive</c> true; <c>isActive</c> true → <c>IsInactive</c> false.</summary>
    private static bool? ReadIsInactive(JsonElement company)
    {
        if (!TryGetProperty(company, out var active, "isActive"))
            return null;

        if (active.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return !active.GetBoolean();
        if (active.ValueKind == JsonValueKind.Number && active.TryGetInt32(out var n))
            return n == 0;
        if (active.ValueKind == JsonValueKind.String)
        {
            var s = active.GetString();
            if (bool.TryParse(s, out var b))
                return !b;
        }

        return null;
    }

    private static void ReadPageDetails(
        JsonElement payload,
        out string? nextPageUrl,
        out int? count,
        out int? requestCount)
    {
        nextPageUrl = null;
        count = null;
        requestCount = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetProperty(payload, out var details, "pageDetails")
            || details.ValueKind != JsonValueKind.Object)
            return;

        nextPageUrl = ReadString(details, "nextPageUrl");
        count = ReadInt(details, "count");
        requestCount = ReadInt(details, "requestCount");
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

    private static string? ReadNonEmptyString(JsonElement obj, params string[] names)
        => ReadString(obj, names);

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

    private static IEnumerable<JsonElement> EnumerateCompanies(JsonElement payload)
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
            throw new InvalidOperationException("Autotask MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Autotask MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Autotask MCP tool error: {errText ?? payload.GetRawText()}");
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

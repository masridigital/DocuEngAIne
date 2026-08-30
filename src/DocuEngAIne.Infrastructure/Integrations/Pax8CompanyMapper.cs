using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>pax8_list_companies</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Live list is <c>{ "content": [ company, ... ], "page": { size, totalElements,
/// totalPages, number } }</c>. Page is 0-indexed. Each company uses <c>id</c> (UUID string),
/// <c>name</c>, optional <c>website</c>, <c>status</c> (Active|Inactive|Deleted),
/// <c>address.city</c>, and <c>address.stateOrProvince</c>.
/// Skip rows missing <c>id</c> or <c>name</c>. Do not pass a <c>status</c> filter on the pull —
/// Inactive and Deleted still map (<c>IsInactive</c> true) so <c>SkipInactive</c> can drop them in
/// sync. Compact schema <c>size</c> defaults to 50 (max 200). Page on raw <c>content</c> length and
/// <c>page.number</c> / <c>page.totalPages</c>, never mapped count. Stop when content is empty,
/// <c>number + 1 &gt;= totalPages</c>, or the current page is last.
/// </summary>
public static class Pax8CompanyMapper
{
    public const string ToolName = "pax8_list_companies";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(string mcpBody)
        => MapCompanies(mcpBody, out _, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(
        string mcpBody,
        out int? pageNumber,
        out int? totalPages,
        out int rowCount)
        => MapCompanies(mcpBody, out pageNumber, out totalPages, out rowCount, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count and <c>page.number</c> / <c>page.totalPages</c>, or one unmappable row ends the pull and
    /// the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(
        string mcpBody,
        out int? pageNumber,
        out int? totalPages,
        out int rowCount,
        out int? pageSize)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        ReadPage(payload, out pageNumber, out totalPages, out pageSize);
        var companies = new List<ExternalCompanyDto>();
        rowCount = 0;
        foreach (var company in EnumerateCompanies(payload))
        {
            rowCount++;
            var mapped = MapCompany(company);
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
            ["page"] = page < 0 ? 0 : page,
            ["size"] = size,
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
        const int maxPages = 500;
        for (var page = 0; page < maxPages; page++)
        {
            var args = BuildArgumentsJson(page, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapCompanies(body, out var pageNumber, out var totalPages, out var rowCount);
            companies.AddRange(mapped);
            // Raw content length / page.number+totalPages, never mapped rows — see AutotaskCompanyMapper.
            // Empty content, number+1 >= totalPages, or the current page being last ends the pull.
            if (rowCount == 0)
                break;
            var number = pageNumber ?? page;
            if (totalPages is int pages && number + 1 >= pages)
                break;
            if (totalPages is null)
                break;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapCompany(JsonElement company)
    {
        if (company.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(company, "id");
        var name = ReadString(company, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        ReadAddress(company, out var city, out var state);
        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            City: city,
            State: state,
            Website: ReadString(company, "website"),
            IsInactive: ReadIsInactive(company));
    }

    /// <summary>
    /// <c>status</c> Inactive or Deleted → <c>IsInactive</c> true; Active → false.
    /// Missing / unknown status is left null so SkipInactive does not invent a drop.
    /// </summary>
    private static bool? ReadIsInactive(JsonElement company)
    {
        var status = ReadString(company, "status");
        if (string.IsNullOrWhiteSpace(status))
            return null;

        if (status.Equals("Active", StringComparison.OrdinalIgnoreCase))
            return false;
        if (status.Equals("Inactive", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Deleted", StringComparison.OrdinalIgnoreCase))
            return true;

        return null;
    }

    private static void ReadAddress(JsonElement company, out string? city, out string? state)
    {
        city = null;
        state = null;
        if (!TryGetProperty(company, out var address, "address") || address.ValueKind != JsonValueKind.Object)
            return;

        city = ReadString(address, "city");
        state = ReadString(address, "stateOrProvince");
    }

    private static void ReadPage(
        JsonElement payload,
        out int? pageNumber,
        out int? totalPages,
        out int? pageSize)
    {
        pageNumber = null;
        totalPages = null;
        pageSize = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetProperty(payload, out var page, "page")
            || page.ValueKind != JsonValueKind.Object)
            return;

        pageNumber = ReadInt(page, "number");
        totalPages = ReadInt(page, "totalPages");
        pageSize = ReadInt(page, "size");
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

    private static IEnumerable<JsonElement> EnumerateCompanies(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var content, "content")
            && content.ValueKind == JsonValueKind.Array)
        {
            return content.EnumerateArray();
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
            throw new InvalidOperationException("Pax8 MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Pax8 MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Pax8 MCP tool error: {errText ?? payload.GetRawText()}");
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

    /// <summary>
    /// MCP tool wrappers put <c>{ type, text }</c> items in <c>content</c>. The Pax8 envelope also
    /// uses <c>content</c> for company objects, which have no <c>text</c> — those are left as-is.
    /// </summary>
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

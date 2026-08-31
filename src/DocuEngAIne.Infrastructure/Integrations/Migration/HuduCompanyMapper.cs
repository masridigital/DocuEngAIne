using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;


namespace DocuEngAIne.Infrastructure.Integrations.Migration;

/// <summary>
/// Maps StackJack Compact <c>hudu_list_companies</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Live list objects use <c>id</c>, <c>name</c>, <c>slug</c>, <c>website</c>,
/// <c>city</c>, <c>state</c>, <c>address_line_1</c>, and optional <c>archived</c>/<c>inactive</c>.
/// Compact schema: <c>page</c> (default 1), <c>pageSize</c> (default 25, max 1000).
/// </summary>
public static class HuduCompanyMapper
{
    public const string ToolName = McpServerDefaults.HuduListCompaniesTool;
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(string mcpBody)
        => MapCompanies(mcpBody, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows Hudu returned, which is NOT
    /// the number mapped — a company with no id or name is dropped. Paging must turn on the raw
    /// count, or one unmappable row ends the pull early.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapCompanies(string mcpBody, out int rowCount)
    {
        var payload = HuduMcpPayload.Unwrap(mcpBody, "Hudu");
        var companies = new List<ExternalCompanyDto>();
        rowCount = 0;
        foreach (var company in HuduMcpPayload.EnumerateNamedArray(payload, "companies", "items", "data", "records", "results"))
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
            ["page"] = page < 1 ? 1 : page,
            ["pageSize"] = size,
        });
    }

    private static ExternalCompanyDto? MapCompany(JsonElement company)
    {
        if (company.ValueKind != JsonValueKind.Object)
            return null;

        var id = HuduMcpPayload.ReadString(company, "id", "company_id", "companyId");
        var name = HuduMcpPayload.ReadString(company, "name", "company_name", "companyName");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        var website = HuduMcpPayload.ReadString(company, "website", "website_url", "websiteUrl", "url");
        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            Slug: HuduMcpPayload.ReadString(company, "slug"),
            PrimaryDomain: CompanyIdentity.NormalizeDomain(website)
                ?? CompanyIdentity.NormalizeDomain(HuduMcpPayload.ReadString(company, "primary_domain", "primaryDomain", "domain")),
            City: HuduMcpPayload.ReadString(company, "city"),
            State: HuduMcpPayload.ReadString(company, "state"),
            Website: website,
            Address: HuduMcpPayload.ReadString(company, "address_line_1", "addressLine1", "address1", "address"),
            IsInactive: ReadInactive(company));
    }

    private static bool? ReadInactive(JsonElement company)
    {
        var archived = HuduMcpPayload.ReadBool(company, "archived", "is_archived", "isArchived");
        if (archived is true)
            return true;

        var inactive = HuduMcpPayload.ReadBool(company, "inactive", "is_inactive", "isInactive");
        if (inactive is not null)
            return inactive;

        var active = HuduMcpPayload.ReadBool(company, "active", "is_active", "isActive");
        if (active is not null)
            return !active;

        return archived;
    }
}

using System.Text.Json;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class CippTenantMapperTests
{
    // Live Compact cipp_list_tenants JSON array (field names exact; names can stay).
    public const string LiveCompactListFixture = """
        [{"customerId":"8c65106e-9e7e-45d4-b55a-3cbd4b415a08","displayName":"ADROC Capital, LLC","defaultDomainName":"adroccap.com","Excluded":false,"domains":""},{"customerId":"f7812296-5bce-41dc-8102-b1b270e7c4c7","displayName":"*Partner Tenant","defaultDomainName":"masridigital.com","Excluded":false,"domains":"PartnerTenant"},{"customerId":"deadbeef-0000-0000-0000-000000000001","displayName":"Gone Co","defaultDomainName":"gone.example","Excluded":true,"domains":""}]
        """;

    [Fact]
    public void MapTenants_LiveCompactList_MapsCustomerIdAndDefaultDomain_SkipsPartner()
    {
        var companies = CippTenantMapper.MapTenants(LiveCompactListFixture);

        Assert.Equal(2, companies.Count);

        var adroc = companies[0];
        Assert.Equal("8c65106e-9e7e-45d4-b55a-3cbd4b415a08", adroc.ExternalId);
        Assert.Equal("ADROC Capital, LLC", adroc.Name);
        Assert.Equal("adroccap.com", adroc.PrimaryDomain);
        Assert.False(adroc.IsInactive);
        Assert.Null(adroc.Slug);
        Assert.Null(adroc.Website);
        Assert.Null(adroc.City);
        Assert.Null(adroc.State);
        Assert.Null(adroc.Address);

        var gone = companies[1];
        Assert.Equal("deadbeef-0000-0000-0000-000000000001", gone.ExternalId);
        Assert.Equal("Gone Co", gone.Name);
        Assert.Equal("gone.example", gone.PrimaryDomain);
        Assert.True(gone.IsInactive);

        Assert.DoesNotContain(companies, c => c.Name == "*Partner Tenant");
        Assert.DoesNotContain(companies, c => c.ExternalId == "f7812296-5bce-41dc-8102-b1b270e7c4c7");
        Assert.DoesNotContain(companies, c => c.PrimaryDomain == "masridigital.com");
    }

    [Fact]
    public void MapTenants_JsonRpcContentTextArray_UnwrapsToTenantList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var companies = CippTenantMapper.MapTenants(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("8c65106e-9e7e-45d4-b55a-3cbd4b415a08", companies[0].ExternalId);
        Assert.Equal("ADROC Capital, LLC", companies[0].Name);
        Assert.Equal("adroccap.com", companies[0].PrimaryDomain);
    }

    [Fact]
    public void MapTenants_DefaultDomainNameWinsOverInitialDomainName()
    {
        const string json = """
            [{"customerId":"8c65106e-9e7e-45d4-b55a-3cbd4b415a08","displayName":"ADROC Capital, LLC","defaultDomainName":"adroccap.com","initialDomainName":"adroc.onmicrosoft.com","Excluded":false,"domains":""}]
            """;

        var company = Assert.Single(CippTenantMapper.MapTenants(json));
        Assert.Equal("adroccap.com", company.PrimaryDomain);
    }

    [Fact]
    public void BuildArgumentsJson_PassesTenantsOnlyStringTrue()
    {
        var args = CippTenantMapper.BuildArgumentsJson();
        Assert.Contains("\"tenantsOnly\":\"true\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearCache", args, StringComparison.Ordinal);
        Assert.DoesNotContain("TenantsOnly", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNo", args, StringComparison.Ordinal);
    }
}

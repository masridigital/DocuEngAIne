using System.Text.Json;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class DattoSiteMapperTests
{
    // Compact datto_list_sites list as [{uid,name}]. Field names from the catalog; no live Datto call.
    public const string SitesArrayFixture = """
        [{"uid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","name":"Adroc Capital"},{"uid":"11111111-2222-3333-4444-555555555555","name":"Masri Digital"}]
        """;

    // Catalog-equivalent wrapper: the list lives in sites[].
    public const string SitesWrapperFixture = """
        {"sites":[{"uid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","name":"Adroc Capital"},{"uid":"11111111-2222-3333-4444-555555555555","name":"Masri Digital"}]}
        """;

    [Fact]
    public void MapCompanies_SitesArray_MapsUidAndName_DoesNotInventInactive()
    {
        var companies = DattoSiteMapper.MapCompanies(SitesArrayFixture);

        Assert.Equal(2, companies.Count);

        var adroc = companies[0];
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Null(adroc.IsInactive);
        Assert.Null(adroc.Slug);
        Assert.Null(adroc.PrimaryDomain);
        Assert.Null(adroc.Website);
        Assert.Null(adroc.City);
        Assert.Null(adroc.State);
        Assert.Null(adroc.Address);

        Assert.Equal("11111111-2222-3333-4444-555555555555", companies[1].ExternalId);
        Assert.Equal("Masri Digital", companies[1].Name);
        Assert.All(companies, c => Assert.Null(c.IsInactive));
    }

    [Fact]
    public void MapCompanies_SitesWrapper_MapsUidAndName()
    {
        var companies = DattoSiteMapper.MapCompanies(SitesWrapperFixture);

        Assert.Equal(2, companies.Count);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", companies[0].ExternalId);
        Assert.Equal("Adroc Capital", companies[0].Name);
        Assert.Equal("11111111-2222-3333-4444-555555555555", companies[1].ExternalId);
        Assert.Equal("Masri Digital", companies[1].Name);
        Assert.All(companies, c => Assert.Null(c.IsInactive));
    }

    [Fact]
    public void MapCompanies_Skips_Empty_Uid_Or_Name()
    {
        const string json = """
            [{"uid":"","name":"No Uid"},{"uid":"bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee","name":""},{"name":"Missing Uid"},{"uid":"cccccccc-cccc-dddd-eeee-ffffffffffff"},{"uid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","name":"Adroc Capital"}]
            """;

        var adroc = Assert.Single(DattoSiteMapper.MapCompanies(json));
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", adroc.ExternalId);
    }

    [Fact]
    public void MapCompanies_JsonRpcContentTextArray_UnwrapsToSiteList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = SitesArrayFixture } } },
        });

        var companies = DattoSiteMapper.MapCompanies(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", companies[0].ExternalId);
        Assert.Equal("Adroc Capital", companies[0].Name);
        Assert.Null(companies[0].IsInactive);
    }

    [Fact]
    public void MapCompanies_JsonRpcContentTextWrapper_UnwrapsToSites()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = SitesWrapperFixture } } },
        });

        var companies = DattoSiteMapper.MapCompanies(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", companies[0].ExternalId);
        Assert.Equal("Adroc Capital", companies[0].Name);
    }
}

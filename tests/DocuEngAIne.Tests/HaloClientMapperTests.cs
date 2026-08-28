using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class HaloClientMapperTests
{
    // Live Compact halo_list_clients wrapper + client keys (anonymized names; field names exact).
    private const string LiveCompactListFixture = """
        {
          "page_no": 1,
          "page_size": 2,
          "record_count": 54,
          "clients": [
            {
              "id": 12,
              "name": "Masri",
              "inactive": false,
              "ref": "",
              "override_org_website": "https://example.com"
            },
            {
              "id": 29,
              "name": "Inactive Co",
              "inactive": true,
              "ref": "",
              "override_org_website": ""
            }
          ]
        }
        """;

    [Fact]
    public void MapClients_LiveCompactList_MapsIdWebsiteAndInactive()
    {
        var companies = HaloClientMapper.MapClients(LiveCompactListFixture);

        Assert.Equal(2, companies.Count);

        var masri = companies[0];
        Assert.Equal("12", masri.ExternalId);
        Assert.Equal("Masri", masri.Name);
        Assert.Equal("https://example.com", masri.Website);
        Assert.False(masri.IsInactive);
        Assert.Null(masri.Slug);

        var inactive = companies[1];
        Assert.Equal("29", inactive.ExternalId);
        Assert.True(inactive.IsInactive);
        Assert.Null(inactive.Website);
    }

    [Fact]
    public void MapClients_NonEmptyRef_BecomesSlug()
    {
        const string json = """
            {
              "page_no": 1,
              "page_size": 1,
              "record_count": 1,
              "clients": [
                {
                  "id": 7,
                  "name": "With Ref",
                  "inactive": false,
                  "ref": "with-ref",
                  "override_org_website": ""
                }
              ]
            }
            """;

        var company = Assert.Single(HaloClientMapper.MapClients(json));
        Assert.Equal("7", company.ExternalId);
        Assert.Equal("with-ref", company.Slug);
    }

    [Fact]
    public void MapClients_StoppedWithoutInactive_IsInactive()
    {
        const string json = """
            {
              "clients": [
                {
                  "id": 8,
                  "name": "Stopped Co",
                  "ref": "",
                  "override_org_website": "",
                  "stopped": 1
                }
              ]
            }
            """;

        var company = Assert.Single(HaloClientMapper.MapClients(json));
        Assert.True(company.IsInactive);
    }
}

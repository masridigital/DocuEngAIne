using System.Text.Json;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Integrations.Migration;

namespace DocuEngAIne.Tests;

public class HuduCompanyMapperTests
{
    // Sanitized Compact hudu_list_companies wrapper (Hudu field names exact; names are fixtures).
    public const string LiveCompactListFixture = """
        {
          "companies": [
            {
              "id": 42,
              "name": "ExampleCo",
              "slug": "exampleco",
              "website": "https://example.com",
              "city": "Austin",
              "state": "TX",
              "address_line_1": "100 Main St",
              "archived": false
            },
            {
              "id": 99,
              "name": "Archived Co",
              "slug": "archived-co",
              "website": "",
              "archived": true
            }
          ]
        }
        """;

    [Fact]
    public void MapCompanies_LiveCompactList_MapsIdWebsiteDomainAndArchived()
    {
        var companies = HuduCompanyMapper.MapCompanies(LiveCompactListFixture, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Equal(2, companies.Count);

        var example = companies[0];
        Assert.Equal("42", example.ExternalId);
        Assert.Equal("ExampleCo", example.Name);
        Assert.Equal("exampleco", example.Slug);
        Assert.Equal("https://example.com", example.Website);
        Assert.Equal("example.com", example.PrimaryDomain);
        Assert.Equal("Austin", example.City);
        Assert.Equal("TX", example.State);
        Assert.Equal("100 Main St", example.Address);
        Assert.False(example.IsInactive);

        var archived = companies[1];
        Assert.Equal("99", archived.ExternalId);
        Assert.True(archived.IsInactive);
        Assert.Null(archived.Website);
    }

    [Fact]
    public void MapCompanies_JsonRpcContentText_UnwrapsToCompanyList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var companies = HuduCompanyMapper.MapCompanies(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("42", companies[0].ExternalId);
    }

    [Fact]
    public void MapCompanies_DropsRowsWithoutIdOrName()
    {
        const string json = """
            {
              "companies": [
                { "id": 1 },
                { "name": "No Id" },
                { "id": 2, "name": "Kept" }
              ]
            }
            """;

        var companies = HuduCompanyMapper.MapCompanies(json, out var rowCount);
        Assert.Equal(3, rowCount);
        var kept = Assert.Single(companies);
        Assert.Equal("2", kept.ExternalId);
    }

    [Fact]
    public void BuildArgumentsJson_UsesCompactPageAndPageSize()
    {
        var args = HuduCompanyMapper.BuildArgumentsJson(2, 50);
        using var doc = JsonDocument.Parse(args);
        Assert.Equal(2, doc.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, doc.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(McpServerDefaults.HuduListCompaniesTool, HuduCompanyMapper.ToolName);
    }

}

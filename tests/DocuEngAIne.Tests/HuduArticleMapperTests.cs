using System.Text.Json;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Integrations.Migration;

namespace DocuEngAIne.Tests;

public class HuduArticleMapperTests
{
    // Sanitized Compact hudu_list_articles (field names exact; no credentials).
    public const string LiveCompactListFixture = """
        {
          "articles": [
            {
              "id": 7,
              "name": "VPN Setup",
              "slug": "vpn-setup",
              "content": "<p>Use the company gateway.</p>",
              "company_id": 42,
              "folder_id": 3,
              "folder_name": "Networking",
              "draft": false
            },
            {
              "id": 8,
              "name": "Draft Note",
              "slug": "draft-note",
              "company_id": 42,
              "draft": true
            }
          ]
        }
        """;

    public const string LiveCompactGetFixture = """
        {
          "article": {
            "id": 8,
            "name": "Draft Note",
            "slug": "draft-note",
            "content": "<p>Internal only.</p>",
            "company_id": 42,
            "folder_id": 3,
            "draft": true
          }
        }
        """;

    [Fact]
    public void MapArticles_LiveCompactList_MapsIdCompanyFolderAndDraft()
    {
        var articles = HuduArticleMapper.MapArticles(LiveCompactListFixture, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Equal(2, articles.Count);

        var vpn = articles[0];
        Assert.Equal("7", vpn.ExternalId);
        Assert.Equal("VPN Setup", vpn.Title);
        Assert.Equal("vpn-setup", vpn.Slug);
        Assert.Equal("<p>Use the company gateway.</p>", vpn.Content);
        Assert.Equal("42", vpn.CompanyExternalId);
        Assert.Equal("3", vpn.FolderExternalId);
        Assert.Equal("Networking", vpn.FolderName);
        Assert.False(vpn.Draft);

        var draft = articles[1];
        Assert.Equal("8", draft.ExternalId);
        Assert.True(draft.Draft);
        Assert.Null(draft.Content);
    }

    [Fact]
    public void MapArticle_GetWrapper_ReadsContent()
    {
        var article = HuduArticleMapper.MapArticle(LiveCompactGetFixture);
        Assert.NotNull(article);
        Assert.Equal("8", article.ExternalId);
        Assert.Equal("<p>Internal only.</p>", article.Content);
        Assert.True(article.Draft);
    }

    [Fact]
    public void MapArticles_JsonRpcContentText_Unwraps()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var articles = HuduArticleMapper.MapArticles(wrapped);
        Assert.Equal(2, articles.Count);
        Assert.Equal("7", articles[0].ExternalId);
    }

    [Fact]
    public void BuildArguments_ListUsesPage_GetUsesIntegerId()
    {
        var list = HuduArticleMapper.BuildListArgumentsJson(3, 25);
        using (var doc = JsonDocument.Parse(list))
        {
            Assert.Equal(3, doc.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(25, doc.RootElement.GetProperty("pageSize").GetInt32());
        }

        var get = HuduArticleMapper.BuildGetArgumentsJson("8");
        using (var doc = JsonDocument.Parse(get))
        {
            Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("id").ValueKind);
            Assert.Equal(8, doc.RootElement.GetProperty("id").GetInt32());
        }

        Assert.Equal(McpServerDefaults.HuduListArticlesTool, HuduArticleMapper.ListToolName);
        Assert.Equal(McpServerDefaults.HuduGetArticleTool, HuduArticleMapper.GetToolName);
    }

    [Fact]
    public void MapFolders_LiveCompactList_MapsIdNameAndCompany()
    {
        const string json = """
            {
              "folders": [
                { "id": 3, "name": "Networking", "company_id": 42 }
              ]
            }
            """;

        var folder = Assert.Single(HuduFolderMapper.MapFolders(json));
        Assert.Equal("3", folder.ExternalId);
        Assert.Equal("Networking", folder.Name);
        Assert.Equal("42", folder.CompanyExternalId);
        Assert.Equal(McpServerDefaults.HuduListFoldersTool, HuduFolderMapper.ToolName);
    }
}

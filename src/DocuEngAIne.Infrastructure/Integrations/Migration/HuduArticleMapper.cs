using System.Text.Json;
using DocuEngAIne.Core.Mcp;

namespace DocuEngAIne.Infrastructure.Integrations.Migration;

/// <summary>
/// Maps StackJack Compact <c>hudu_list_articles</c> / <c>hudu_get_article</c> JSON to article records.
/// Live list objects use <c>id</c>, <c>name</c>, <c>slug</c>, <c>content</c>, <c>company_id</c>,
/// <c>folder_id</c>, and <c>draft</c>. List summaries may omit HTML — map <c>hudu_get_article</c>
/// fixture JSON separately. Password entities are never mapped here.
/// Compact schema: <c>page</c> / <c>pageSize</c> on list; <c>id</c> (integer) on get.
/// The migrate endpoint does not call Compact; it maps sanitized fixture / catalog JSON.
/// </summary>
public static class HuduArticleMapper
{
    public const string ListToolName = McpServerDefaults.HuduListArticlesTool;
    public const string GetToolName = McpServerDefaults.HuduGetArticleTool;
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<HuduArticleRecord> MapArticles(string mcpBody)
        => MapArticles(mcpBody, out _);

    public static IReadOnlyList<HuduArticleRecord> MapArticles(string mcpBody, out int rowCount)
    {
        var payload = HuduMcpPayload.Unwrap(mcpBody, "Hudu");
        var articles = new List<HuduArticleRecord>();
        rowCount = 0;

        if (TrySingleArticle(payload, out var single))
        {
            rowCount = 1;
            if (single is not null)
                articles.Add(single);
            return articles;
        }

        foreach (var article in HuduMcpPayload.EnumerateNamedArray(payload, "articles", "items", "data", "records", "results"))
        {
            rowCount++;
            var mapped = MapArticle(article);
            if (mapped is not null)
                articles.Add(mapped);
        }

        return articles;
    }

    public static HuduArticleRecord? MapArticle(string mcpBody)
    {
        var mapped = MapArticles(mcpBody, out _);
        return mapped.Count == 0 ? null : mapped[0];
    }

    public static string BuildListArgumentsJson(int page, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["page"] = page < 1 ? 1 : page,
            ["pageSize"] = size,
        });
    }

    public static string BuildGetArgumentsJson(string externalId)
    {
        if (int.TryParse(externalId, out var id))
            return JsonSerializer.Serialize(new Dictionary<string, object?> { ["id"] = id });
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["id"] = externalId });
    }

    private static bool TrySingleArticle(JsonElement payload, out HuduArticleRecord? article)
    {
        article = null;
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        if (HuduMcpPayload.TryGetProperty(payload, out var wrapped, "article") && wrapped.ValueKind == JsonValueKind.Object)
        {
            article = MapArticle(wrapped);
            return true;
        }

        // A get-article body is a single object with id + name, not a list wrapper.
        if (HuduMcpPayload.ReadString(payload, "id") is not null
            && HuduMcpPayload.ReadString(payload, "name", "title") is not null
            && !HuduMcpPayload.TryGetProperty(payload, out _, "articles"))
        {
            article = MapArticle(payload);
            return true;
        }

        return false;
    }

    private static HuduArticleRecord? MapArticle(JsonElement article)
    {
        if (article.ValueKind != JsonValueKind.Object)
            return null;

        var id = HuduMcpPayload.ReadString(article, "id", "article_id", "articleId");
        var title = HuduMcpPayload.ReadString(article, "name", "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        var folderName = HuduMcpPayload.ReadString(article, "folder_name", "folderName");
        if (folderName is null
            && HuduMcpPayload.TryGetProperty(article, out var folder, "folder")
            && folder.ValueKind == JsonValueKind.Object)
        {
            folderName = HuduMcpPayload.ReadString(folder, "name");
        }

        return new HuduArticleRecord(
            ExternalId: id,
            Title: title.Trim(),
            Content: HuduMcpPayload.ReadString(article, "content", "body", "html"),
            Slug: HuduMcpPayload.ReadString(article, "slug"),
            CompanyExternalId: HuduMcpPayload.ReadString(article, "company_id", "companyId", "companyid"),
            FolderExternalId: HuduMcpPayload.ReadString(article, "folder_id", "folderId", "folderid"),
            FolderName: folderName,
            Draft: HuduMcpPayload.ReadBool(article, "draft", "is_draft", "isDraft") == true);
    }
}

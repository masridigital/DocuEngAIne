using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;

namespace DocuEngAIne.Infrastructure.Integrations.Migration;

/// <summary>
/// Maps StackJack Compact <c>hudu_list_folders</c> JSON to folder records so imported articles
/// land in a company <c>DocumentFolder</c> named from Hudu (the KB folder name, else "Hudu").
/// </summary>
public static class HuduFolderMapper
{
    public const string ToolName = McpServerDefaults.HuduListFoldersTool;
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<HuduFolderRecord> MapFolders(string mcpBody)
        => MapFolders(mcpBody, out _);

    public static IReadOnlyList<HuduFolderRecord> MapFolders(string mcpBody, out int rowCount)
    {
        var payload = HuduMcpPayload.Unwrap(mcpBody, "Hudu");
        var folders = new List<HuduFolderRecord>();
        rowCount = 0;
        foreach (var folder in HuduMcpPayload.EnumerateNamedArray(payload, "folders", "items", "data", "records", "results"))
        {
            rowCount++;
            var mapped = MapFolder(folder);
            if (mapped is not null)
                folders.Add(mapped);
        }

        return folders;
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

    public static async Task<IReadOnlyList<HuduFolderRecord>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var folders = new List<HuduFolderRecord>();
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(page, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapFolders(body, out var rowCount);
            folders.AddRange(mapped);
            if (rowCount == 0 || rowCount < size)
                break;
        }

        return folders;
    }

    private static HuduFolderRecord? MapFolder(JsonElement folder)
    {
        if (folder.ValueKind != JsonValueKind.Object)
            return null;

        var id = HuduMcpPayload.ReadString(folder, "id", "folder_id", "folderId");
        var name = HuduMcpPayload.ReadString(folder, "name", "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new HuduFolderRecord(
            ExternalId: id,
            Name: name.Trim(),
            CompanyExternalId: HuduMcpPayload.ReadString(folder, "company_id", "companyId", "companyid"));
    }
}

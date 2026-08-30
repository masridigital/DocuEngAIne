using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>keeper_scim_list_users</c> JSON (vendor passthrough, often
/// JSON-RPC wrapped) to KeeperLink DTOs. Compact Keeper has no vault record list and no
/// password tools — this mapper never stores secrets and always leaves
/// <see cref="ExternalKeeperLinkDto.KeeperRecordUrl"/> null.
/// Live list is a SCIM <c>ListResponse</c> whose rows live in <c>Resources</c>. Each user
/// uses <c>id</c> (SCIM user id — not a vault UID), <c>userName</c> →
/// <see cref="ExternalKeeperLinkDto.UsernameHint"/>, and <c>displayName</c> (fallback
/// <c>userName</c>) → <see cref="ExternalKeeperLinkDto.Name"/>.
/// Skip rows missing <c>id</c> or a name. Compact schema <c>startIndex</c> is 1-based;
/// <c>count</c> max 500. Page on raw <c>Resources</c> length / <c>itemsPerPage</c>, never
/// mapped count. Next page is <c>startIndex + itemsPerPage</c> while
/// <c>startIndex + itemsPerPage - 1 &lt; totalResults</c>.
/// Do not call provision, password, or SCIM write tools.
/// </summary>
public static class KeeperScimUserMapper
{
    public const string ToolName = "keeper_scim_list_users";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    public static IReadOnlyList<ExternalKeeperLinkDto> MapUsers(string mcpBody)
        => MapUsers(mcpBody, out _, out _, out _, out _);

    public static IReadOnlyList<ExternalKeeperLinkDto> MapUsers(
        string mcpBody,
        out int? startIndex,
        out int? totalResults,
        out int rowCount)
        => MapUsers(mcpBody, out startIndex, out totalResults, out rowCount, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned,
    /// which is NOT the number mapped — rows missing a required field are dropped. Paging must
    /// turn on the raw count and <c>totalResults</c>, or one unmappable row ends the pull.
    /// </summary>
    public static IReadOnlyList<ExternalKeeperLinkDto> MapUsers(
        string mcpBody,
        out int? startIndex,
        out int? totalResults,
        out int rowCount,
        out int? itemsPerPage)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        ReadListMeta(payload, out startIndex, out totalResults, out itemsPerPage);
        var links = new List<ExternalKeeperLinkDto>();
        rowCount = 0;
        foreach (var user in EnumerateUsers(payload))
        {
            rowCount++;
            var mapped = MapUser(user);
            if (mapped is not null)
                links.Add(mapped);
        }
        return links;
    }

    public static string BuildArgumentsJson(int startIndex, int count = DefaultPageSize)
    {
        var size = Math.Clamp(count, 1, MaxPageSize);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["startIndex"] = startIndex < 1 ? 1 : startIndex,
            ["count"] = size,
        });
    }

    public static async Task<IReadOnlyList<ExternalKeeperLinkDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var links = new List<ExternalKeeperLinkDto>();
        var startIndex = 1;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(startIndex, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapUsers(body, out var pageStart, out var totalResults, out var rowCount, out var itemsPerPage);
            links.AddRange(mapped);
            // Raw Resources length, never mapped rows — see NinjaOrganizationMapper for the same hazard.
            if (rowCount == 0)
                break;
            var pageNow = pageStart ?? startIndex;
            var pageLen = itemsPerPage ?? rowCount;
            var next = pageNow + pageLen;
            if (totalResults is int total && next > total)
                break;
            if (rowCount < size)
                break;
            startIndex = next;
        }
        return links;
    }

    private static ExternalKeeperLinkDto? MapUser(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(user, "id");
        var userName = ReadString(user, "userName");
        var displayName = ReadString(user, "displayName");
        var name = displayName ?? userName;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        // id is the SCIM user id, not a vault record UID. Compact has no vault list.
        return new ExternalKeeperLinkDto(
            ExternalId: id,
            Name: name.Trim(),
            UsernameHint: userName,
            KeeperRecordUrl: null);
    }

    private static void ReadListMeta(
        JsonElement payload,
        out int? startIndex,
        out int? totalResults,
        out int? itemsPerPage)
    {
        startIndex = ReadInt(payload, "startIndex");
        totalResults = ReadInt(payload, "totalResults");
        itemsPerPage = ReadInt(payload, "itemsPerPage");
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !TryGetProperty(obj, out var value, name))
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

    private static IEnumerable<JsonElement> EnumerateUsers(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var resources, "Resources")
            && resources.ValueKind == JsonValueKind.Array)
        {
            return resources.EnumerateArray();
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
            throw new InvalidOperationException("Keeper MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Keeper MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Keeper MCP tool error: {errText ?? payload.GetRawText()}");
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

using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>halo_list_users</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to contact DTOs. Live list objects use <c>id</c>, <c>name</c>, <c>emailaddress</c>,
/// <c>client_id</c>, <c>site_id</c>, and <c>inactive</c>. List is the sync source — do not call
/// <c>halo_get_user</c> or <c>halo_search_users</c>.
/// Compact <c>includeInactive</c> defaults to false; pull always sends <c>includeInactive</c> true
/// so <c>SkipInactive</c> can be applied later in sync, not here. Optional <c>clientId</c> scopes
/// the list. Page on raw row count, never mapped count.
/// </summary>
public static class HaloUserMapper
{
    public const string ToolName = "halo_list_users";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static IReadOnlyList<ExternalContactDto> MapUsers(string mcpBody)
        => MapUsers(mcpBody, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows Halo returned, which is NOT the
    /// number mapped — a user with no id or no name is dropped. Paging must turn on the raw count, or
    /// one unmappable user ends the pull and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalContactDto> MapUsers(string mcpBody, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var contacts = new List<ExternalContactDto>();
        rowCount = 0;
        foreach (var user in EnumerateUsers(payload))
        {
            rowCount++;
            var mapped = MapUser(user);
            if (mapped is not null)
                contacts.Add(mapped);
        }
        return contacts;
    }

    public static string BuildArgumentsJson(int pageNo, int pageSize = DefaultPageSize, int? clientId = null)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["pageNo"] = pageNo < 1 ? 1 : pageNo,
            ["pageSize"] = size,
            ["includeInactive"] = true,
        };
        if (clientId is int id)
            args["clientId"] = id;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalContactDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        int? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var contacts = new List<ExternalContactDto>();
        const int maxPages = 500;
        for (var pageNo = 1; pageNo <= maxPages; pageNo++)
        {
            var args = BuildArgumentsJson(pageNo, size, clientId);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var page = MapUsers(body, out var rowCount);
            contacts.AddRange(page);
            // Raw rows, never mapped rows: a user with no id or name is dropped, and testing the
            // mapped count would read that short page as the last one and abandon the rest.
            if (rowCount < size)
                break;
        }
        return contacts;
    }

    private static ExternalContactDto? MapUser(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(user, "id", "userid", "user_id", "userId");
        var name = ReadString(user, "name", "user_name", "userName");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalContactDto(
            ExternalId: id,
            ClientExternalId: ReadString(user, "client_id", "clientid", "clientId"),
            Name: name.Trim(),
            Email: ReadString(user, "emailaddress", "email_address", "emailAddress", "email"),
            SiteExternalId: ReadString(user, "site_id", "siteid", "siteId"),
            IsInactive: ReadInactive(user));
    }

    private static bool? ReadInactive(JsonElement user)
    {
        if (TryGetProperty(user, out var inactive, "inactive", "isinactive", "is_inactive", "isInactive"))
        {
            if (inactive.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return inactive.GetBoolean();
            if (inactive.ValueKind == JsonValueKind.Number && inactive.TryGetInt32(out var n))
                return n != 0;
            if (inactive.ValueKind == JsonValueKind.String)
            {
                var s = inactive.GetString();
                if (bool.TryParse(s, out var b))
                    return b;
                if (string.Equals(s, "inactive", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(s, "active", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        if (TryGetProperty(user, out var active, "active", "isactive", "is_active", "isActive"))
        {
            if (active.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return !active.GetBoolean();
        }

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
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind != JsonValueKind.Object)
            return [];

        foreach (var name in new[] { "users", "items", "data", "records", "value", "results" })
        {
            if (TryGetProperty(payload, out var arr, name) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray();
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
            throw new InvalidOperationException("Halo MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Halo MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Halo MCP tool error: {errText ?? payload.GetRawText()}");
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

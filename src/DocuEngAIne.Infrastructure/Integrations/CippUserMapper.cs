using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>cipp_list_users</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to contact DTOs (People / contacts — not Entra <c>User</c> operators). Compact requires
/// camelCase <c>tenantFilter</c> — the tenant domain from <c>cipp_list_tenants</c>
/// (<c>defaultDomainName</c>). List objects use Graph user names: <c>id</c>,
/// <c>displayName</c>, <c>userPrincipalName</c>, <c>accountEnabled</c>. License status
/// (<c>assignedLicenses</c> / <c>LicJoined</c>) is not stored (no matching
/// <see cref="ExternalContactDto"/> properties). Users have no company id —
/// <c>ClientExternalId</c> is stamped from the caller (CIPP <c>customerId</c>).
/// Skip Partner / Excluded is tenant-level, not here. Skip rows missing
/// <c>id</c>, <c>userPrincipalName</c>, or a name. List is the sync source —
/// one shot, no pagination. A JSON array is the list.
/// </summary>
public static class CippUserMapper
{
    public const string ToolName = "cipp_list_users";

    public static IReadOnlyList<ExternalContactDto> MapUsers(string mcpBody, string clientExternalId)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var contacts = new List<ExternalContactDto>();
        foreach (var user in EnumerateUsers(payload))
        {
            var mapped = MapUser(user, clientExternalId);
            if (mapped is not null)
                contacts.Add(mapped);
        }
        return contacts;
    }

    public static string BuildArgumentsJson(string tenantFilter)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["tenantFilter"] = tenantFilter,
        });

    public static async Task<IReadOnlyList<ExternalContactDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        string tenantFilter,
        string clientExternalId,
        CancellationToken cancellationToken = default)
    {
        var args = BuildArgumentsJson(tenantFilter);
        var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
        return MapUsers(body, clientExternalId);
    }

    private static ExternalContactDto? MapUser(JsonElement user, string clientExternalId)
    {
        if (user.ValueKind != JsonValueKind.Object)
            return null;

        if (string.IsNullOrWhiteSpace(clientExternalId))
            return null;

        var id = ReadString(user, "id");
        var upn = ReadString(user, "userPrincipalName", "user_principal_name");
        var name = ReadString(user, "displayName", "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(upn) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalContactDto(
            ExternalId: id,
            ClientExternalId: clientExternalId,
            Name: name.Trim(),
            Email: upn,
            SiteExternalId: null,
            IsInactive: ReadInactive(user));
    }

    private static bool? ReadInactive(JsonElement user)
    {
        if (!TryGetProperty(user, out var enabled, "accountEnabled", "account_enabled"))
            return null;

        if (enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return !enabled.GetBoolean();
        if (enabled.ValueKind == JsonValueKind.Number && enabled.TryGetInt32(out var n))
            return n == 0;
        if (enabled.ValueKind == JsonValueKind.String)
        {
            var s = enabled.GetString();
            if (bool.TryParse(s, out var b))
                return !b;
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

        value = default;
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateUsers(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

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
            throw new InvalidOperationException("CIPP MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"CIPP MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"CIPP MCP tool error: {errText ?? payload.GetRawText()}");
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

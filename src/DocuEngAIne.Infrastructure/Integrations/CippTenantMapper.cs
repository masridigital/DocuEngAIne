using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>cipp_list_tenants</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Live list objects use <c>customerId</c>, <c>displayName</c>,
/// <c>defaultDomainName</c>, <c>Excluded</c>, <c>domains</c>, and <c>initialDomainName</c>.
/// A JSON array is the list. The partner row (<c>displayName</c> "*Partner Tenant" /
/// <c>domains</c> "PartnerTenant") is not a customer. List is the sync source — one shot, no pagination.
/// Compact schema uses camelCase <c>tenantsOnly</c> as a string-typed boolean.
/// </summary>
public static class CippTenantMapper
{
    public const string ToolName = "cipp_list_tenants";
    public const string TenantsOnlyArgument = "true";

    public static IReadOnlyList<ExternalCompanyDto> MapTenants(string mcpBody)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var companies = new List<ExternalCompanyDto>();
        foreach (var tenant in EnumerateTenants(payload))
        {
            var mapped = MapTenant(tenant);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
    }

    public static string BuildArgumentsJson()
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["tenantsOnly"] = TenantsOnlyArgument,
        });

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        CancellationToken cancellationToken = default)
    {
        var args = BuildArgumentsJson();
        var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
        return MapTenants(body);
    }

    private static ExternalCompanyDto? MapTenant(JsonElement tenant)
    {
        if (tenant.ValueKind != JsonValueKind.Object)
            return null;

        if (IsPartnerTenant(tenant))
            return null;

        var id = ReadString(tenant, "customerId");
        var name = ReadString(tenant, "displayName");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        var primaryDomain = ReadString(tenant, "defaultDomainName")
            ?? ReadString(tenant, "initialDomainName");

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            PrimaryDomain: primaryDomain,
            IsInactive: ReadExcluded(tenant));
    }

    private static bool IsPartnerTenant(JsonElement tenant)
    {
        var domains = ReadString(tenant, "domains");
        if (string.Equals(domains, "PartnerTenant", StringComparison.OrdinalIgnoreCase))
            return true;

        var name = ReadString(tenant, "displayName");
        return string.Equals(name, "*Partner Tenant", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ReadExcluded(JsonElement tenant)
    {
        if (!TryGetProperty(tenant, out var excluded, "Excluded"))
            return null;

        if (excluded.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return excluded.GetBoolean();
        if (excluded.ValueKind == JsonValueKind.Number && excluded.TryGetInt32(out var n))
            return n != 0;
        if (excluded.ValueKind == JsonValueKind.String)
        {
            var s = excluded.GetString();
            if (bool.TryParse(s, out var b))
                return b;
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

    private static IEnumerable<JsonElement> EnumerateTenants(JsonElement payload)
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

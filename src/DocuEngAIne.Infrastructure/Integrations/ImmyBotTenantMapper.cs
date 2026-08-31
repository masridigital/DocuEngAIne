using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>immy_list_tenants</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Catalog list is the tenants (managed customer organizations); each row uses
/// <c>id</c> and <c>name</c>. A JSON array is the list, or a catalog-equivalent
/// <c>{ "tenants": [ ... ] }</c> / <c>{ "clients": [ ... ] }</c> wrapper. Skip rows missing
/// <c>id</c> or <c>name</c>. There is no inactive flag on the list schema — do not invent
/// <c>IsInactive</c> from activate/deactivate tools or parent/Azure fields.
/// List is the sync source — do not call <c>immy_get_tenants</c>, <c>immy_list_computers</c>,
/// or <c>immy_list_provider_links_clients</c> (that last one needs a provider-link id and is
/// not the company list). Compact schema <c>page</c> is 1-based; <c>pageSize</c> max 100.
/// Omit <c>filters</c>/<c>sorts</c>. Page on raw row count, never mapped count.
/// </summary>
public static class ImmyBotTenantMapper
{
    public const string ToolName = "immy_list_tenants";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static IReadOnlyList<ExternalCompanyDto> MapTenants(string mcpBody)
        => MapTenants(mcpBody, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count, or one unmappable row ends the pull and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapTenants(string mcpBody, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var companies = new List<ExternalCompanyDto>();
        rowCount = 0;
        foreach (var tenant in EnumerateTenants(payload))
        {
            rowCount++;
            var mapped = MapTenant(tenant);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
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

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var companies = new List<ExternalCompanyDto>();
        var page = 1;
        const int maxPages = 500;
        for (var i = 1; i <= maxPages; i++)
        {
            var args = BuildArgumentsJson(page, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapTenants(body, out var rowCount);
            companies.AddRange(mapped);
            // Raw row count, never mapped rows — see NinjaOrganizationMapper for the same hazard.
            if (rowCount == 0 || rowCount < size)
                break;
            page++;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapTenant(JsonElement tenant)
    {
        if (tenant.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(tenant, "id");
        var name = ReadString(tenant, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim());
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

    private static IEnumerable<JsonElement> EnumerateTenants(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind != JsonValueKind.Object)
            return [];

        foreach (var name in new[] { "tenants", "clients" })
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
            throw new InvalidOperationException("ImmyBot MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"ImmyBot MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"ImmyBot MCP tool error: {errText ?? payload.GetRawText()}");
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

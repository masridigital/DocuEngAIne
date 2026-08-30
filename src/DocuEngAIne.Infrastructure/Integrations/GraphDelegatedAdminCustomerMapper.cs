using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>graph_list_delegated_admin_customers</c> JSON (vendor passthrough,
/// often JSON-RPC wrapped) to company DTOs. This is the CSP company-list source — one row per
/// customer the partner can administer. Live Graph list is
/// <c>{ "value": [ customer, ... ], "@odata.nextLink"? }</c>. Each customer uses <c>id</c>,
/// <c>tenantId</c>, and <c>displayName</c>. <c>ExternalId</c> is <c>tenantId</c> when present,
/// otherwise <c>id</c>. Skip rows missing an id/tenantId or a name. Skip the partner Home tenant
/// when <c>homeTenantId</c> matches <c>tenantId</c> or <c>id</c>.
/// Do not call <c>graph_list_partner_customers</c> here — that mapper is enrich only, not default
/// create. Do not call <c>graph_list_delegated_admin_relationships</c>.
/// Compact schema <c>maxItems</c> defaults to 1000 (1–1000); sync caps at 50. Optional OData
/// <c>filter</c> on the first page. Cursor is wrapper <c>@odata.nextLink</c>, passed verbatim as
/// <c>nextLink</c>. When <c>nextLink</c> is supplied, <c>maxItems</c> and <c>filter</c> are omitted.
/// Page on raw <c>value</c> length, never mapped count. Stop when value is empty or
/// <c>@odata.nextLink</c> is null/empty.
/// A GDAP / active flag on the row (when present) maps to <c>IsInactive</c>. <c>SkipInactive</c>
/// keeps only customers with active GDAP when that flag is present; otherwise every mappable row
/// is returned and skip is left to sync.
/// </summary>
public static class GraphDelegatedAdminCustomerMapper
{
    public const string ToolName = "graph_list_delegated_admin_customers";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<ExternalCompanyDto> MapCustomers(string mcpBody)
        => MapCustomers(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapCustomers(string mcpBody, out string? nextLink)
        => MapCustomers(mcpBody, out nextLink, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows Graph returned, which is
    /// NOT the number mapped — Home tenant rows and rows missing a required field are dropped.
    /// Paging must turn on the raw count and <c>@odata.nextLink</c>, or one unmappable row ends
    /// the pull and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapCustomers(
        string mcpBody,
        out string? nextLink,
        out int rowCount)
        => MapCustomers(mcpBody, out nextLink, out rowCount, homeTenantId: null, skipInactive: false);

    public static IReadOnlyList<ExternalCompanyDto> MapCustomers(
        string mcpBody,
        out string? nextLink,
        out int rowCount,
        string? homeTenantId,
        bool skipInactive = false)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        nextLink = ReadNextLink(payload);
        var companies = new List<ExternalCompanyDto>();
        rowCount = 0;
        foreach (var customer in EnumerateCustomers(payload))
        {
            rowCount++;
            var mapped = MapCustomer(customer, homeTenantId, skipInactive);
            if (mapped is not null)
                companies.Add(mapped);
        }

        return companies;
    }

    public static string BuildArgumentsJson(
        string? nextLink,
        int maxItems = DefaultPageSize,
        string? filter = null,
        string? entraTenant = null)
    {
        // Schema: when nextLink is supplied, follow it verbatim. Omit maxItems / filter so the
        // previous page's @odata.nextLink is the only paging argument.
        if (!string.IsNullOrWhiteSpace(nextLink))
        {
            var cursor = new Dictionary<string, object?> { ["nextLink"] = nextLink };
            if (!string.IsNullOrWhiteSpace(entraTenant))
                cursor["entraTenant"] = entraTenant;
            return JsonSerializer.Serialize(cursor);
        }

        var size = Math.Clamp(maxItems, 1, MaxPageSize);
        var args = new Dictionary<string, object?> { ["maxItems"] = size };
        if (!string.IsNullOrWhiteSpace(filter))
            args["filter"] = filter;
        if (!string.IsNullOrWhiteSpace(entraTenant))
            args["entraTenant"] = entraTenant;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        string? homeTenantId = null,
        bool skipInactive = false,
        string? filter = null,
        string? entraTenant = null,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var companies = new List<ExternalCompanyDto>();
        string? nextLink = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(nextLink, size, filter, entraTenant);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapCustomers(body, out var pageNextLink, out var rowCount, homeTenantId, skipInactive);
            companies.AddRange(mapped);
            // Raw value length / @odata.nextLink, never mapped rows — see AutotaskCompanyMapper.
            // Empty value or a null/empty nextLink ends the pull. Do not stop on a short page
            // while nextLink is present: Graph pages can be smaller than maxItems.
            if (rowCount == 0)
                break;
            if (string.IsNullOrWhiteSpace(pageNextLink))
                break;
            nextLink = pageNextLink;
        }

        return companies;
    }

    private static ExternalCompanyDto? MapCustomer(JsonElement customer, string? homeTenantId, bool skipInactive)
    {
        if (customer.ValueKind != JsonValueKind.Object)
            return null;

        var tenantId = ReadString(customer, "tenantId");
        var id = ReadString(customer, "id");
        var externalId = tenantId ?? id;
        var name = ReadString(customer, "displayName");
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(name))
            return null;

        if (IsHomeTenant(externalId, id, homeTenantId))
            return null;

        var isInactive = ReadIsInactive(customer);
        if (skipInactive && isInactive == true)
            return null;

        return new ExternalCompanyDto(
            ExternalId: externalId,
            Name: name.Trim(),
            IsInactive: isInactive);
    }

    private static bool IsHomeTenant(string externalId, string? id, string? homeTenantId)
    {
        if (string.IsNullOrWhiteSpace(homeTenantId))
            return false;

        return string.Equals(externalId, homeTenantId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, homeTenantId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Optional GDAP / active flag. <c>status</c> / <c>gdapStatus</c> / <c>relationshipStatus</c>
    /// <c>active</c> or <c>expiring</c> → <c>IsInactive</c> false; known inactive GDAP statuses →
    /// true. Boolean <c>isActive</c> / <c>hasActiveGdap</c> invert the same way. Missing / unknown
    /// leaves <c>IsInactive</c> null so SkipInactive does not invent a drop.
    /// </summary>
    private static bool? ReadIsInactive(JsonElement customer)
    {
        if (TryGetProperty(customer, out var flag, "hasActiveGdap", "hasActiveGDAP"))
        {
            var parsed = ReadBooleanFlag(flag);
            if (parsed is bool hasActive)
                return !hasActive;
        }

        if (TryGetProperty(customer, out var active, "isActive"))
        {
            var parsed = ReadBooleanFlag(active);
            if (parsed is bool isActive)
                return !isActive;
        }

        var status = ReadString(customer, "gdapStatus", "relationshipStatus", "status");
        if (string.IsNullOrWhiteSpace(status))
            return null;

        if (status.Equals("active", StringComparison.OrdinalIgnoreCase)
            || status.Equals("expiring", StringComparison.OrdinalIgnoreCase))
            return false;

        if (status.Equals("expired", StringComparison.OrdinalIgnoreCase)
            || status.Equals("terminated", StringComparison.OrdinalIgnoreCase)
            || status.Equals("terminating", StringComparison.OrdinalIgnoreCase)
            || status.Equals("approvalPending", StringComparison.OrdinalIgnoreCase)
            || status.Equals("created", StringComparison.OrdinalIgnoreCase)
            || status.Equals("activating", StringComparison.OrdinalIgnoreCase)
            || status.Equals("approved", StringComparison.OrdinalIgnoreCase))
            return true;

        return null;
    }

    private static bool? ReadBooleanFlag(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n != 0;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static string? ReadNextLink(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        return ReadString(payload, "@odata.nextLink", "odata.nextLink", "nextLink");
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

    private static IEnumerable<JsonElement> EnumerateCustomers(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var value, "value")
            && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray();
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
            throw new InvalidOperationException("Graph MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Graph MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Graph MCP tool error: {errText ?? payload.GetRawText()}");
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

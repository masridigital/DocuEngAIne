using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>graph_list_partner_customers</c> JSON (vendor passthrough, often
/// JSON-RPC wrapped) for <em>enrichment</em> only — company name / domain against a tenant id
/// already created from <see cref="GraphDelegatedAdminCustomerMapper"/>. Do not use this list as
/// the default company-create source: Partner Center includes billed customers with no GDAP.
/// Live list is <c>{ "items": [ customer, ... ], "continuationToken"? }</c>. Each customer uses
/// <c>id</c> plus <c>companyProfile.tenantId</c>, <c>companyProfile.companyName</c>, and
/// <c>companyProfile.domain</c>. <c>ExternalId</c> is tenantId when present, otherwise id.
/// Skip rows missing an id or a name. Skip the partner Home tenant when <c>homeTenantId</c>
/// matches. Compact schema <c>size</c> defaults to 100 (1–500). Cursor is
/// <c>continuationToken</c> from the previous page, passed verbatim. Page on raw <c>items</c>
/// length, never mapped count. Stop when items are empty or the token is null/empty.
/// </summary>
public static class GraphPartnerCustomerMapper
{
    public const string ToolName = "graph_list_partner_customers";
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    public static IReadOnlyList<ExternalCompanyDto> MapCustomers(string mcpBody)
        => MapCustomers(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapCustomers(string mcpBody, out string? continuationToken)
        => MapCustomers(mcpBody, out continuationToken, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows Partner Center returned,
    /// which is NOT the number mapped. Paging must turn on the raw count and
    /// <c>continuationToken</c>.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapCustomers(
        string mcpBody,
        out string? continuationToken,
        out int rowCount)
        => MapCustomers(mcpBody, out continuationToken, out rowCount, homeTenantId: null);

    public static IReadOnlyList<ExternalCompanyDto> MapCustomers(
        string mcpBody,
        out string? continuationToken,
        out int rowCount,
        string? homeTenantId)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        continuationToken = ReadContinuationToken(payload);
        var companies = new List<ExternalCompanyDto>();
        rowCount = 0;
        foreach (var customer in EnumerateCustomers(payload))
        {
            rowCount++;
            var mapped = MapCustomer(customer, homeTenantId);
            if (mapped is not null)
                companies.Add(mapped);
        }

        return companies;
    }

    public static string BuildArgumentsJson(string? continuationToken, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?> { ["size"] = size };
        if (!string.IsNullOrWhiteSpace(continuationToken))
            args["continuationToken"] = continuationToken;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        string? homeTenantId = null,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var companies = new List<ExternalCompanyDto>();
        string? token = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(token, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapCustomers(body, out var nextToken, out var rowCount, homeTenantId);
            companies.AddRange(mapped);
            if (rowCount == 0 || string.IsNullOrWhiteSpace(nextToken))
                break;
            token = nextToken;
        }

        return companies;
    }

    private static ExternalCompanyDto? MapCustomer(JsonElement customer, string? homeTenantId)
    {
        if (customer.ValueKind != JsonValueKind.Object)
            return null;

        TryGetProperty(customer, out var profile, "companyProfile");
        var tenantId = profile.ValueKind == JsonValueKind.Object
            ? ReadString(profile, "tenantId")
            : null;
        var id = ReadString(customer, "id");
        var externalId = tenantId ?? id;
        var name = profile.ValueKind == JsonValueKind.Object
            ? ReadString(profile, "companyName")
            : null;
        name ??= ReadString(customer, "companyName", "displayName");
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(name))
            return null;

        if (!string.IsNullOrWhiteSpace(homeTenantId)
            && (string.Equals(externalId, homeTenantId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, homeTenantId, StringComparison.OrdinalIgnoreCase)))
            return null;

        var domain = profile.ValueKind == JsonValueKind.Object
            ? ReadString(profile, "domain")
            : ReadString(customer, "domain");

        return new ExternalCompanyDto(
            ExternalId: externalId,
            Name: name.Trim(),
            PrimaryDomain: domain);
    }

    private static string? ReadContinuationToken(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        return ReadString(payload, "continuationToken", "ContinuationToken");
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

        if (payload.ValueKind != JsonValueKind.Object)
            return [];

        foreach (var name in new[] { "items", "value" })
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

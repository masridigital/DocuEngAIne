using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>tl_search_child_organizations</c> JSON (vendor passthrough, often
/// JSON-RPC wrapped) to company DTOs. There is no <c>tl_list_organizations</c> — this search is
/// the customer list. Catalog input requires <c>orderBy</c>, <c>pageNumber</c> (1-based), and
/// <c>pageSize</c> (capped at 500; ThreatLocker documents no maximum). There is no cursor.
/// Optional <c>managedOrganizationId</c> scopes the parent; omit it to use the connector default.
/// Optional <c>includeAllChildren</c> walks the whole tree. Vendor publishes no response schema;
/// fixtures use catalog field names: <c>organizationId</c> (GUID used as
/// <c>managedOrganizationId</c> on later calls), <c>displayName</c> / <c>name</c>, and
/// <c>domains</c> (first entry → PrimaryDomain). Skip rows missing id or name. There is no
/// catalog inactive flag; do not invent <c>IsInactive</c>. Page on raw row count and
/// <c>totalItems</c>, never mapped count.
/// </summary>
public static class ThreatLockerOrganizationMapper
{
    public const string ToolName = "tl_search_child_organizations";
    public const string DefaultOrderBy = "name";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody)
        => MapOrganizations(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody, out int? totalItems)
        => MapOrganizations(mcpBody, out totalItems, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count and <c>totalItems</c>, or one unmappable row ends the pull and the run still reports
    /// Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody, out int? totalItems, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        totalItems = ReadTotalItems(payload);
        var companies = new List<ExternalCompanyDto>();
        rowCount = 0;
        foreach (var org in EnumerateOrganizations(payload))
        {
            rowCount++;
            var mapped = MapOrganization(org);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
    }

    public static string BuildArgumentsJson(
        int pageNumber,
        int pageSize = DefaultPageSize,
        string? managedOrganizationId = null,
        bool includeAllChildren = true)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["orderBy"] = DefaultOrderBy,
            ["pageNumber"] = pageNumber < 1 ? 1 : pageNumber,
            ["pageSize"] = size,
            ["includeAllChildren"] = includeAllChildren,
            ["isAscending"] = true,
        };
        if (!string.IsNullOrWhiteSpace(managedOrganizationId))
            args["managedOrganizationId"] = managedOrganizationId;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        string? managedOrganizationId = null,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var companies = new List<ExternalCompanyDto>();
        var rawSeen = 0;
        const int maxPages = 500;
        for (var pageNumber = 1; pageNumber <= maxPages; pageNumber++)
        {
            var args = BuildArgumentsJson(pageNumber, size, managedOrganizationId);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapOrganizations(body, out var totalItems, out var rowCount);
            companies.AddRange(mapped);
            // Raw rows + totalItems, never mapped rows — see NinjaOrganizationMapper for the same hazard.
            // No cursor: increment pageNumber. Empty / short page, or rawSeen >= totalItems, ends the pull.
            if (rowCount == 0)
                break;
            rawSeen += rowCount;
            if (totalItems is int total && rawSeen >= total)
                break;
            if (rowCount < size)
                break;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapOrganization(JsonElement org)
    {
        if (org.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(org, "organizationId", "id");
        var name = ReadString(org, "displayName", "organizationName", "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            PrimaryDomain: ReadPrimaryDomain(org));
    }

    /// <summary><c>domains[0]</c> when present and non-empty; later entries are ignored.</summary>
    private static string? ReadPrimaryDomain(JsonElement org)
    {
        if (TryGetProperty(org, out var domains, "domains") && domains.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in domains.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return null;
                var value = item.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            return null;
        }

        return ReadString(org, "domain");
    }

    private static int? ReadTotalItems(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        return ReadInt(payload, "totalItems", "totalRows");
    }

    private static int? ReadInt(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names))
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

    private static IEnumerable<JsonElement> EnumerateOrganizations(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "items", "data", "results", "organizations" })
            {
                if (TryGetProperty(payload, out var arr, name) && arr.ValueKind == JsonValueKind.Array)
                    return arr.EnumerateArray();
            }
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
            throw new InvalidOperationException("ThreatLocker MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"ThreatLocker MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"ThreatLocker MCP tool error: {errText ?? payload.GetRawText()}");
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

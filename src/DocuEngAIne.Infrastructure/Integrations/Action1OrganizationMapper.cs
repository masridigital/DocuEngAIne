using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>action1_list_organizations</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Live list objects use <c>id</c> (GUID string), <c>name</c>, <c>description</c>,
/// and <c>enterprise_id</c>. The list is a <c>ResultPage</c> whose rows live in <c>items</c>.
/// The MSP default org (<c>id</c> equals <c>enterprise_id</c>, or <c>description</c> "Default organization")
/// is not a customer. There is no inactive flag; do not invent <c>IsInactive</c>.
/// Ignore <c>type</c>, <c>self</c>, <c>description</c> (except the skip rule), and <c>enterprise_id</c>
/// after the skip check. Compact schema <c>pageSize</c> defaults to 50 (max 100); <c>admin</c> is true.
/// Cursor is <c>from</c> = <c>next_page.from</c> (integer). An empty-string / missing / null
/// <c>next_page</c> ends the pull. <c>total_items</c> and <c>limit</c> are strings.
/// </summary>
public static class Action1OrganizationMapper
{
    public const string ToolName = "action1_list_organizations";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody)
        => MapOrganizations(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody, out int? nextPageFrom)
        => MapOrganizations(mcpBody, out nextPageFrom, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — default orgs and rows missing a required field are dropped. Paging must
    /// turn on the raw count and <c>next_page.from</c>, or one unmappable row ends the pull and the
    /// run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody, out int? nextPageFrom, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var companies = new List<ExternalCompanyDto>();
        nextPageFrom = ReadNextPageFrom(payload);
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

    public static string BuildArgumentsJson(int? from, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["admin"] = true,
            ["pageSize"] = size,
        };
        if (from is int cursor)
            args["from"] = cursor;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var companies = new List<ExternalCompanyDto>();
        int? from = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(from, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapOrganizations(body, out var nextFrom, out var rowCount);
            companies.AddRange(mapped);
            // Raw rows + next_page.from, never mapped rows — see NinjaOrganizationMapper for the same hazard.
            // Empty string / missing / null next_page is the end; a short mapped page is not.
            if (rowCount == 0)
                break;
            if (nextFrom is not int next)
                break;
            from = next;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapOrganization(JsonElement org)
    {
        if (org.ValueKind != JsonValueKind.Object)
            return null;

        if (IsDefaultOrganization(org))
            return null;

        var id = ReadString(org, "id");
        var name = ReadString(org, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim());
    }

    /// <summary>
    /// MSP default org: same idea as CIPP skipping Partner Tenant. Live rows use either
    /// <c>id == enterprise_id</c> or <c>description == "Default organization"</c>.
    /// </summary>
    private static bool IsDefaultOrganization(JsonElement org)
    {
        var id = ReadString(org, "id");
        var enterpriseId = ReadString(org, "enterprise_id");
        if (!string.IsNullOrWhiteSpace(id)
            && string.Equals(id, enterpriseId, StringComparison.OrdinalIgnoreCase))
            return true;

        var description = ReadString(org, "description");
        return string.Equals(description, "Default organization", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Live Compact ends with <c>next_page: ""</c> (empty string, not a missing key). More pages
    /// send an object <c>{"from": integer}</c>. Missing / null / empty string all mean stop.
    /// </summary>
    private static int? ReadNextPageFrom(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetProperty(payload, out var nextPage, "next_page"))
            return null;

        if (nextPage.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (nextPage.ValueKind == JsonValueKind.String)
            return null;

        if (nextPage.ValueKind != JsonValueKind.Object
            || !TryGetProperty(nextPage, out var from, "from"))
            return null;

        if (from.ValueKind == JsonValueKind.Number && from.TryGetInt32(out var n))
            return n;
        if (from.ValueKind == JsonValueKind.String && int.TryParse(from.GetString(), out var parsed))
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
        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var items, "items")
            && items.ValueKind == JsonValueKind.Array)
        {
            return items.EnumerateArray();
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
            throw new InvalidOperationException("Action1 MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Action1 MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Action1 MCP tool error: {errText ?? payload.GetRawText()}");
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

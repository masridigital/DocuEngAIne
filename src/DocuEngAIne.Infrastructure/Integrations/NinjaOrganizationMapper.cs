using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>ninja_list_organizations</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Live list objects use <c>id</c>, <c>name</c>, and <c>nodeApprovalMode</c>
/// (MANUAL|AUTOMATIC). List is the sync source — do not call <c>ninja_get_organization</c>.
/// The list has no inactive, website, or description fields; do not invent <c>IsInactive</c>
/// from <c>nodeApprovalMode</c>.
/// </summary>
public static class NinjaOrganizationMapper
{
    public const string ToolName = "ninja_list_organizations";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody)
        => MapOrganizations(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody, out int? lastOrganizationId)
        => MapOrganizations(mcpBody, out lastOrganizationId, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count, or one unmappable row ends the pull and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody, out int? lastOrganizationId, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var companies = new List<ExternalCompanyDto>();
        lastOrganizationId = null;
        rowCount = 0;
        foreach (var org in EnumerateOrganizations(payload))
        {
            rowCount++;
            if (TryReadId(org, out var id))
                lastOrganizationId = id;
            var mapped = MapOrganization(org);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
    }

    public static string BuildArgumentsJson(int? afterOrganizationId, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["pageSize"] = size,
        };
        if (afterOrganizationId is int after)
            args["after"] = after;
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
        int? after = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(after, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapOrganizations(body, out var lastId, out var rowCount);
            companies.AddRange(mapped);
            // Raw rows, never mapped rows: an organization with no name is dropped, and testing the
            // mapped count would read that short page as the last one and abandon the rest.
            if (rowCount == 0 || rowCount < size)
                break;
            if (lastId is null)
                break;
            after = lastId;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapOrganization(JsonElement org)
    {
        if (org.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(org, "id");
        var name = ReadString(org, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim());
    }

    private static bool TryReadId(JsonElement org, out int id)
    {
        id = 0;
        if (org.ValueKind != JsonValueKind.Object || !TryGetProperty(org, out var value, "id"))
            return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out id))
            return true;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out id);
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

    private static IEnumerable<JsonElement> EnumerateOrganizations(JsonElement payload)
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
            throw new InvalidOperationException("NinjaOne MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"NinjaOne MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"NinjaOne MCP tool error: {errText ?? payload.GetRawText()}");
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

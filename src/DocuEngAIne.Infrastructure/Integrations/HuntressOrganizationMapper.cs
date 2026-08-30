using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>huntress_list_organizations</c> JSON (vendor passthrough, often
/// JSON-RPC wrapped) to company DTOs. Compact / Huntress list envelope is
/// <c>{ "organizations": [ { id, name, key, ... }, ... ], "pagination": { next_page_token? } }</c>.
/// Huntress uses "organizations" for customer accounts/tenants — there is no
/// <c>huntress_list_accounts</c>. Do not call <c>huntress_get_organization</c>,
/// <c>huntress_get_account</c>, or reseller-only <c>huntress_list_managed_accounts</c>.
/// Map <c>id</c> (numeric) → <c>ExternalId</c>, <c>name</c> → <c>Name</c>, optional
/// <c>key</c> → <c>Slug</c>. Skip rows missing <c>id</c> or <c>name</c>. There is no
/// inactive flag; do not invent <c>IsInactive</c>. Ignore <c>account_id</c>, counts,
/// <c>identity_provider_tenant_id</c>, <c>report_recipients</c>, and timestamps.
/// Compact schema <c>limit</c> defaults to 50 (API max 500). Cursor is
/// <c>pagination.next_page_token</c> passed back as <c>pageToken</c>. Omit
/// <c>name</c>/<c>key</c>/<c>sort*</c> filters so the pull is the full list. Page on
/// raw <c>organizations</c> length, never mapped count. Stop when the list is empty
/// or <c>next_page_token</c> is absent / empty.
/// </summary>
public static class HuntressOrganizationMapper
{
    public const string ToolName = "huntress_list_organizations";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody)
        => MapOrganizations(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody, out string? nextPageToken)
        => MapOrganizations(mcpBody, out nextPageToken, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows Huntress returned, which is
    /// NOT the number mapped — rows missing <c>id</c> or <c>name</c> are dropped. Paging must turn
    /// on the raw count and <c>pagination.next_page_token</c>, or one unmappable row ends the pull
    /// and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(
        string mcpBody,
        out string? nextPageToken,
        out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        nextPageToken = ReadNextPageToken(payload);
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

    public static string BuildArgumentsJson(string? pageToken, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["limit"] = size,
        };
        if (!string.IsNullOrWhiteSpace(pageToken))
            args["pageToken"] = pageToken;
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
        string? pageToken = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(pageToken, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapOrganizations(body, out var nextPageToken, out var rowCount);
            companies.AddRange(mapped);
            // Raw organizations length + next_page_token, never mapped rows — see SlideClientMapper.
            // Empty list or a missing / empty next_page_token ends the pull; a short mapped page is not.
            if (rowCount == 0)
                break;
            if (string.IsNullOrWhiteSpace(nextPageToken))
                break;
            pageToken = nextPageToken;
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
            Name: name.Trim(),
            Slug: ReadString(org, "key"));
    }

    /// <summary>
    /// Compact cursor is <c>pagination.next_page_token</c>. Missing / null / empty string all mean
    /// stop. Do not invent a token from <c>next_page_url</c>.
    /// </summary>
    private static string? ReadNextPageToken(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetProperty(payload, out var pagination, "pagination")
            || pagination.ValueKind != JsonValueKind.Object)
            return null;

        return ReadString(pagination, "next_page_token");
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
            && TryGetProperty(payload, out var organizations, "organizations")
            && organizations.ValueKind == JsonValueKind.Array)
        {
            return organizations.EnumerateArray();
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
            throw new InvalidOperationException("Huntress MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Huntress MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Huntress MCP tool error: {errText ?? payload.GetRawText()}");
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

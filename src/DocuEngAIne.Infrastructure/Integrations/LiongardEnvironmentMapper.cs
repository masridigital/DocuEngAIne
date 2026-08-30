using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>liongard_list_environments</c> JSON (vendor passthrough, often JSON-RPC
/// wrapped) to company DTOs. Compact catalog prefix is <c>liongard_</c>. Environments are the
/// company source — do not call <c>liongard_list_inspectors_v1</c> (inspectors are data-source
/// definitions, not customers) and do not call <c>liongard_get_environment</c>.
/// Vendor envelope is <c>{ "Success": true, "Data": [ environment, ... ], "Pagination": { ... } }</c>.
/// Each environment uses <c>ID</c> (integer), <c>Name</c>, optional <c>ShortName</c> (slug),
/// and optional <c>Website</c>. Skip rows missing <c>ID</c> or <c>Name</c>, and skip
/// Archive / inactive (<c>Status</c> 0, <c>Inactive</c>, <c>Archive</c>, <c>Archived</c>).
/// <c>Status</c> 1 / <c>Active</c> maps as active. Ignore <c>Description</c>, <c>Parent</c>,
/// <c>Tier</c>, <c>Visible</c>, <c>ServiceProviderID</c>, and inspector counts.
/// Compact schema takes FLAT <c>page</c> / <c>pageSize</c> / <c>columns</c> / <c>orderBy</c> only
/// (GET, not POST-for-search). Nested <c>Pagination</c> / <c>Filters</c> is rejected. <c>page</c>
/// is 1-based (default 1); <c>pageSize</c> defaults to 25 (max 2000). Page on raw <c>Data</c>
/// length and <c>Pagination.HasMoreRows</c> / <c>CurrentPage</c> / <c>TotalPages</c>, never mapped
/// count. Connector is not subscribed — fixtures only; do not call live Compact.
/// </summary>
public static class LiongardEnvironmentMapper
{
    public const string ToolName = "liongard_list_environments";
    public const string InspectorToolName = "liongard_list_inspectors_v1";
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 2000;

    public static IReadOnlyList<ExternalCompanyDto> MapEnvironments(string mcpBody)
        => MapEnvironments(mcpBody, out _, out _, out _, out _);

    public static IReadOnlyList<ExternalCompanyDto> MapEnvironments(
        string mcpBody,
        out bool? hasMoreRows,
        out int? currentPage,
        out int? totalPages,
        out int rowCount)
        => MapEnvironments(mcpBody, out hasMoreRows, out currentPage, out totalPages, out rowCount, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field are dropped. Paging must turn on the raw
    /// count and <c>Pagination</c>, or one unmappable row ends the pull and the run still reports
    /// Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapEnvironments(
        string mcpBody,
        out bool? hasMoreRows,
        out int? currentPage,
        out int? totalPages,
        out int rowCount,
        out int? pageSize)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        if (IsExplicitFailure(payload, out var failureMessage))
            throw new InvalidOperationException($"Liongard MCP tool error: {failureMessage}");

        ReadPagination(payload, out hasMoreRows, out currentPage, out totalPages, out pageSize);
        var companies = new List<ExternalCompanyDto>();
        rowCount = 0;
        foreach (var environment in EnumerateEnvironments(payload))
        {
            rowCount++;
            var mapped = MapEnvironment(environment);
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
            var mapped = MapEnvironments(body, out var hasMore, out var currentPage, out var totalPages, out var rowCount, out var vendorPageSize);
            companies.AddRange(mapped);
            // Raw Data length + Pagination, never mapped rows — see NinjaOrganizationMapper.
            // Empty Data, HasMoreRows false, or CurrentPage >= TotalPages ends the pull.
            if (rowCount == 0)
                break;
            if (hasMore == false)
                break;
            var pageNow = currentPage ?? page;
            if (hasMore == true)
            {
                page = pageNow + 1;
                continue;
            }

            if (totalPages is int pages)
            {
                if (pageNow >= pages)
                    break;
                page = pageNow + 1;
                continue;
            }

            var sizeForShort = vendorPageSize ?? size;
            if (rowCount < sizeForShort)
                break;
            page = pageNow + 1;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapEnvironment(JsonElement environment)
    {
        if (environment.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadId(environment);
        var name = ReadString(environment, "Name");
        if (id is null || string.IsNullOrWhiteSpace(name))
            return null;

        if (IsArchiveOrInactive(environment))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            Slug: ReadString(environment, "ShortName"),
            Website: ReadString(environment, "Website"),
            IsInactive: false);
    }

    /// <summary>
    /// Liongard environment ids are integers. Missing / null / non-numeric id is unmappable.
    /// </summary>
    private static string? ReadId(JsonElement environment)
    {
        if (!TryGetProperty(environment, out var value, "ID"))
            return null;

        if (value.ValueKind == JsonValueKind.Number)
            return value.GetRawText();

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        return null;
    }

    /// <summary>
    /// Official schema uses integer <c>Status</c> (example 1 = Active). Compact/docs also describe
    /// <c>Active</c> / <c>Inactive</c> strings. Archive / inactive rows are dropped, not mapped.
    /// </summary>
    private static bool IsArchiveOrInactive(JsonElement environment)
    {
        if (!TryGetProperty(environment, out var status, "Status"))
            return false;

        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var n))
            return n == 0;

        if (status.ValueKind == JsonValueKind.String)
        {
            var s = status.GetString();
            if (string.IsNullOrWhiteSpace(s))
                return false;
            if (s == "0")
                return true;
            if (string.Equals(s, "Inactive", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(s, "Archive", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(s, "Archived", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void ReadPagination(
        JsonElement payload,
        out bool? hasMoreRows,
        out int? currentPage,
        out int? totalPages,
        out int? pageSize)
    {
        hasMoreRows = null;
        currentPage = null;
        totalPages = null;
        pageSize = null;
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetProperty(payload, out var pagination, "Pagination")
            || pagination.ValueKind != JsonValueKind.Object)
            return;

        hasMoreRows = ReadBool(pagination, "HasMoreRows");
        currentPage = ReadInt(pagination, "CurrentPage");
        totalPages = ReadInt(pagination, "TotalPages");
        pageSize = ReadInt(pagination, "PageSize");
    }

    private static bool IsExplicitFailure(JsonElement payload, out string message)
    {
        message = payload.GetRawText();
        if (payload.ValueKind != JsonValueKind.Object
            || !TryGetProperty(payload, out var success, "Success"))
            return false;

        if (success.ValueKind == JsonValueKind.False)
            return true;
        if (success.ValueKind == JsonValueKind.String
            && bool.TryParse(success.GetString(), out var flag)
            && !flag)
            return true;

        return false;
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

    private static bool? ReadBool(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names))
            return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
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

    private static IEnumerable<JsonElement> EnumerateEnvironments(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var data, "Data")
            && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray();
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
            throw new InvalidOperationException("Liongard MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Liongard MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Liongard MCP tool error: {errText ?? payload.GetRawText()}");
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

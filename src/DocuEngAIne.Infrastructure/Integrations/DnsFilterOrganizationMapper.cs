using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>dnsfilter_list_organizations</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. The vendor envelope is JSON:API
/// <c>{ "data": [ { id, type: "organizations", attributes: { name, address, canceled, unique_id, ... } }, ... ],
/// "links": { self } }</c>. Compact prefixes tools <c>dnsfilter_</c>.
/// List is the sync source — do not call <c>dnsfilter_get_organization</c>. Networks
/// (<c>dnsfilter_list_networks</c> / <c>dnsfilter_list_msp_networks</c> / <c>dnsfilter_list_all_networks</c>)
/// are out of scope — orgs map to companies. Do not call <c>dnsfilter_list_all_organizations</c>
/// (the wider "all" counterpart). Do not pass <c>type</c>, <c>name</c>, <c>basicInfo</c>, or MSP
/// filters — <c>SkipInactive</c> is honoured in sync. <c>canceled</c> true → <c>IsInactive</c> true;
/// false → false. Missing <c>canceled</c> is left null. Ignore billing/stripe/feature_flags,
/// vendor <c>external_id</c>, <c>uuid</c>, and <c>relationships.networks</c>.
/// Compact <c>pageNumber</c> is 1-based (omit on the first page); <c>pageSize</c> defaults to 50
/// (StackJack caps at 1000; the vendor publishes no maximum). Page on raw <c>data</c> length,
/// never mapped count. Stop when <c>data</c> is empty or shorter than the requested size.
/// </summary>
public static class DnsFilterOrganizationMapper
{
    public const string ToolName = "dnsfilter_list_organizations";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody)
        => MapOrganizations(mcpBody, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor returned, which is
    /// NOT the number mapped — rows missing a required field or the wrong JSON:API type are dropped.
    /// Paging must turn on the raw count, or one unmappable row ends the pull and the run still
    /// reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalCompanyDto> MapOrganizations(string mcpBody, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
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

    public static string BuildArgumentsJson(int? pageNumber, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["pageSize"] = size,
        };
        if (pageNumber is int n && n > 1)
            args["pageNumber"] = n;
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
        var pageNumber = 1;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(pageNumber == 1 ? null : pageNumber, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapOrganizations(body, out var rowCount);
            companies.AddRange(mapped);
            // Raw data length, never mapped rows — see NinjaOrganizationMapper for the same hazard.
            if (rowCount == 0 || rowCount < size)
                break;
            pageNumber++;
        }
        return companies;
    }

    private static ExternalCompanyDto? MapOrganization(JsonElement org)
    {
        if (org.ValueKind != JsonValueKind.Object)
            return null;

        if (!IsOrganizationType(org))
            return null;

        var attributes = GetAttributes(org);
        var id = ReadString(org, "id") ?? ReadString(attributes, "id");
        var name = ReadString(attributes, "name") ?? ReadString(org, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            Slug: ReadString(attributes, "unique_id", "uniqueId"),
            Address: ReadString(attributes, "address"),
            IsInactive: ReadIsInactive(attributes));
    }

    /// <summary>
    /// JSON:API list rows are <c>type: "organizations"</c>. A missing type still maps when
    /// <c>id</c> + <c>name</c> are present. Networks and other resource types are dropped.
    /// </summary>
    private static bool IsOrganizationType(JsonElement org)
    {
        var type = ReadString(org, "type");
        if (string.IsNullOrWhiteSpace(type))
            return true;

        var normalized = type.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized is "organizations" or "organization";
    }

    /// <summary><c>canceled</c> true → <c>IsInactive</c> true; false → false. Missing is null.</summary>
    private static bool? ReadIsInactive(JsonElement attributes)
    {
        if (!TryGetProperty(attributes, out var canceled, "canceled", "cancelled"))
            return null;

        if (canceled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return canceled.GetBoolean();
        if (canceled.ValueKind == JsonValueKind.Number && canceled.TryGetInt32(out var n))
            return n != 0;
        if (canceled.ValueKind == JsonValueKind.String)
        {
            var s = canceled.GetString();
            if (bool.TryParse(s, out var b))
                return b;
        }

        return null;
    }

    private static JsonElement GetAttributes(JsonElement resource)
    {
        if (resource.ValueKind == JsonValueKind.Object
            && TryGetProperty(resource, out var attributes, "attributes")
            && attributes.ValueKind == JsonValueKind.Object)
        {
            return attributes;
        }

        return resource;
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
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

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var data, "data"))
        {
            if (data.ValueKind == JsonValueKind.Array)
                return data.EnumerateArray();
            if (data.ValueKind == JsonValueKind.Object)
                return [data];
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
            throw new InvalidOperationException("DNSFilter MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"DNSFilter MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"DNSFilter MCP tool error: {errText ?? payload.GetRawText()}");
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

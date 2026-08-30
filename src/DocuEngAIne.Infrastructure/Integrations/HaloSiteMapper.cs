using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>halo_list_sites</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to location DTOs. Compact schema has no <c>pageNo</c> — one shot, full list.
/// Live list objects use <c>id</c>, <c>name</c>, <c>client_id</c>, <c>inactive</c>.
/// Address fields appear when the tool is called with <c>includeAddress=true</c>: flattened
/// <c>delivery_address_line1</c>/<c>delivery_address_line3</c> (Halo line3 is city) or a nested
/// <c>delivery_address</c> object. List is the sync source — do not call <c>halo_get_site</c>.
/// </summary>
public static class HaloSiteMapper
{
    public const string ToolName = "halo_list_sites";

    public static IReadOnlyList<ExternalLocationDto> MapSites(string mcpBody)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var locations = new List<ExternalLocationDto>();
        foreach (var site in EnumerateSites(payload))
        {
            var mapped = MapSite(site);
            if (mapped is not null)
                locations.Add(mapped);
        }
        return locations;
    }

    /// <summary>
    /// Compact <c>halo_list_sites</c> has no page arguments. Always sends <c>includeAddress</c>
    /// so address/city fields are present. Optional <paramref name="clientId"/> filters to one
    /// Halo client. <paramref name="includeInactive"/> defaults on (import them).
    /// </summary>
    public static string BuildArgumentsJson(int? clientId = null, bool includeInactive = true)
    {
        var args = new Dictionary<string, object?>
        {
            ["includeAddress"] = true,
            ["includeInactive"] = includeInactive,
        };
        if (clientId is int id)
            args["clientId"] = id;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalLocationDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int? clientId = null,
        bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        var args = BuildArgumentsJson(clientId, includeInactive);
        var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
        return MapSites(body);
    }

    private static ExternalLocationDto? MapSite(JsonElement site)
    {
        if (site.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(site, "id", "siteid", "site_id", "siteId");
        var clientId = ReadString(site, "client_id", "clientid", "clientId");
        var name = ReadString(site, "name", "sitename", "site_name", "siteName");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalLocationDto(
            ExternalId: id,
            ClientExternalId: clientId,
            Name: name.Trim(),
            Address: ReadAddress(site),
            City: ReadCity(site),
            IsInactive: ReadInactive(site));
    }

    private static string? ReadAddress(JsonElement site)
    {
        var flat = ReadString(site, "address", "address1", "address_1", "addressline1", "delivery_address_line1");
        if (flat is not null)
            return flat;

        if (TryGetNestedAddress(site, out var delivery))
            return ReadString(delivery, "line1", "address", "address1", "addressline1");

        return null;
    }

    private static string? ReadCity(JsonElement site)
    {
        var flat = ReadString(site, "city", "delivery_address_line3");
        if (flat is not null)
            return flat;

        if (TryGetNestedAddress(site, out var delivery))
            return ReadString(delivery, "city", "line3");

        return null;
    }

    private static bool TryGetNestedAddress(JsonElement site, out JsonElement delivery)
    {
        if (TryGetProperty(site, out delivery, "delivery_address", "deliveryaddress")
            && delivery.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        delivery = default;
        return false;
    }

    private static bool? ReadInactive(JsonElement site)
    {
        if (TryGetProperty(site, out var inactive, "inactive", "isinactive", "is_inactive", "isInactive", "inactivesite"))
        {
            if (inactive.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return inactive.GetBoolean();
            if (inactive.ValueKind == JsonValueKind.Number && inactive.TryGetInt32(out var n))
                return n != 0;
            if (inactive.ValueKind == JsonValueKind.String)
            {
                var s = inactive.GetString();
                if (bool.TryParse(s, out var b))
                    return b;
                if (string.Equals(s, "inactive", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(s, "active", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        if (TryGetProperty(site, out var stopped, "stopped"))
        {
            if (stopped.ValueKind == JsonValueKind.Number && stopped.TryGetInt32(out var stoppedN))
                return stoppedN != 0;
            if (stopped.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return stopped.GetBoolean();
        }

        if (TryGetProperty(site, out var active, "active", "isactive", "is_active", "isActive"))
        {
            if (active.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return !active.GetBoolean();
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

    private static IEnumerable<JsonElement> EnumerateSites(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind != JsonValueKind.Object)
            return [];

        foreach (var name in new[] { "sites", "clients", "items", "data", "records", "value", "results" })
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
            throw new InvalidOperationException("Halo MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Halo MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Halo MCP tool error: {errText ?? payload.GetRawText()}");
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

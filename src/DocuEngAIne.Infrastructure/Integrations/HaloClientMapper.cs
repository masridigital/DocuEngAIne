using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>halo_list_clients</c> JSON (vendor passthrough, often JSON-RPC wrapped) to company DTOs.
/// Live list objects use <c>id</c>, <c>name</c>, <c>inactive</c>, <c>ref</c> (slug), <c>override_org_website</c>,
/// and <c>stopped</c>. List is the sync source — do not call <c>halo_get_client</c>.
/// Email/phone override fields are not stored (no matching <see cref="ExternalCompanyDto"/> properties).
/// </summary>
public static class HaloClientMapper
{
    public const string ToolName = "halo_list_clients";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static IReadOnlyList<ExternalCompanyDto> MapClients(string mcpBody)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var companies = new List<ExternalCompanyDto>();
        foreach (var client in EnumerateClients(payload))
        {
            var mapped = MapClient(client);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
    }

    public static string BuildArgumentsJson(int pageNo, bool skipInactive, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["pageNo"] = pageNo < 1 ? 1 : pageNo,
            ["pageSize"] = size,
            ["includeActive"] = true,
            ["includeInactive"] = !skipInactive,
        };
        if (skipInactive)
            args["activeInactive"] = "active";
        return JsonSerializer.Serialize(args);
    }

    private static ExternalCompanyDto? MapClient(JsonElement client)
    {
        if (client.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(client, "id", "clientid", "client_id", "clientId");
        var name = ReadString(client, "name", "clientname", "client_name", "clientName");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            Slug: ReadString(client, "ref"),
            PrimaryDomain: ReadString(client, "primarydomain", "primary_domain", "primaryDomain"),
            City: ReadString(client, "city"),
            State: ReadString(client, "state", "county"),
            Website: ReadString(client, "override_org_website", "website", "websiteurl", "website_url"),
            Address: ReadString(client, "address", "address1", "address_1"),
            IsInactive: ReadInactive(client));
    }

    private static bool? ReadInactive(JsonElement client)
    {
        if (TryGetProperty(client, out var inactive, "inactive", "isinactive", "is_inactive", "isInactive", "inactiveclient"))
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

        if (TryGetProperty(client, out var stopped, "stopped"))
        {
            if (stopped.ValueKind == JsonValueKind.Number && stopped.TryGetInt32(out var stoppedN))
                return stoppedN != 0;
            if (stopped.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return stopped.GetBoolean();
        }

        if (TryGetProperty(client, out var active, "active", "isactive", "is_active", "isActive"))
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

    private static IEnumerable<JsonElement> EnumerateClients(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind != JsonValueKind.Object)
            return [];

        foreach (var name in new[] { "clients", "items", "data", "records", "value", "results" })
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

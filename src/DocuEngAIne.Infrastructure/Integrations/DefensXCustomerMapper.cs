using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>dfx_list_customers</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to company DTOs. Live list is a JSON array of customers. Each customer uses <c>id</c> (UUID string),
/// <c>name</c>, optional <c>domains</c> (first entry → PrimaryDomain), and <c>enabled</c>
/// (<c>enabled</c> false → <c>IsInactive</c> true; <c>enabled</c> true → <c>IsInactive</c> false).
/// Skip rows missing <c>id</c> or <c>name</c>. <c>SkipInactive</c> is honoured in sync, not here.
/// No pagination — one shot, no arguments. A JSON array is the list.
/// </summary>
public static class DefensXCustomerMapper
{
    public const string ToolName = "dfx_list_customers";

    public static IReadOnlyList<ExternalCompanyDto> MapCustomers(string mcpBody)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var companies = new List<ExternalCompanyDto>();
        foreach (var customer in EnumerateCustomers(payload))
        {
            var mapped = MapCustomer(customer);
            if (mapped is not null)
                companies.Add(mapped);
        }
        return companies;
    }

    public static async Task<IReadOnlyList<ExternalCompanyDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        CancellationToken cancellationToken = default)
    {
        var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, null, cancellationToken);
        return MapCustomers(body);
    }

    private static ExternalCompanyDto? MapCustomer(JsonElement customer)
    {
        if (customer.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(customer, "id");
        var name = ReadString(customer, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            PrimaryDomain: ReadPrimaryDomain(customer),
            IsInactive: ReadIsInactive(customer));
    }

    /// <summary><c>domains[0]</c> when present and non-empty; later entries are ignored.</summary>
    private static string? ReadPrimaryDomain(JsonElement customer)
    {
        if (!TryGetProperty(customer, out var domains, "domains") || domains.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in domains.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return null;
            var value = item.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    /// <summary><c>enabled</c> false → <c>IsInactive</c> true; <c>enabled</c> true → <c>IsInactive</c> false.</summary>
    private static bool? ReadIsInactive(JsonElement customer)
    {
        if (!TryGetProperty(customer, out var enabled, "enabled"))
            return null;

        if (enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return !enabled.GetBoolean();
        if (enabled.ValueKind == JsonValueKind.Number && enabled.TryGetInt32(out var n))
            return n == 0;
        if (enabled.ValueKind == JsonValueKind.String)
        {
            var s = enabled.GetString();
            if (bool.TryParse(s, out var b))
                return !b;
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
            throw new InvalidOperationException("DefensX MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"DefensX MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"DefensX MCP tool error: {errText ?? payload.GetRawText()}");
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

using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>cipp_list_devices</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to device DTOs. Compact requires camelCase <c>tenantFilter</c> — the tenant domain from
/// <c>cipp_list_tenants</c> (<c>defaultDomainName</c>). List objects use Graph managedDevice names:
/// <c>id</c>, <c>deviceName</c>, <c>operatingSystem</c>, <c>complianceState</c>, <c>lastSyncDateTime</c>.
/// Compliance and last check-in are not stored (no matching <see cref="ExternalDeviceDto"/> properties).
/// Devices have no organization id — <c>OrganizationExternalId</c> is stamped from the caller
/// (CIPP <c>customerId</c>). Skip Partner / Excluded is tenant-level, not here.
/// List is the sync source — one shot, no pagination. A JSON array is the list.
/// </summary>
public static class CippDeviceMapper
{
    public const string ToolName = "cipp_list_devices";

    public static IReadOnlyList<ExternalDeviceDto> MapDevices(string mcpBody, string organizationExternalId)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var devices = new List<ExternalDeviceDto>();
        foreach (var device in EnumerateDevices(payload))
        {
            var mapped = MapDevice(device, organizationExternalId);
            if (mapped is not null)
                devices.Add(mapped);
        }
        return devices;
    }

    public static string BuildArgumentsJson(string tenantFilter)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["tenantFilter"] = tenantFilter,
        });

    public static async Task<IReadOnlyList<ExternalDeviceDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        string tenantFilter,
        string organizationExternalId,
        CancellationToken cancellationToken = default)
    {
        var args = BuildArgumentsJson(tenantFilter);
        var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
        return MapDevices(body, organizationExternalId);
    }

    private static ExternalDeviceDto? MapDevice(JsonElement device, string organizationExternalId)
    {
        if (device.ValueKind != JsonValueKind.Object)
            return null;

        if (string.IsNullOrWhiteSpace(organizationExternalId))
            return null;

        var id = ReadString(device, "id");
        var name = ReadString(device, "deviceName")
            ?? ReadString(device, "displayName")
            ?? ReadString(device, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalDeviceDto(
            ExternalId: id,
            OrganizationExternalId: organizationExternalId,
            Name: name.Trim(),
            NodeClass: ReadString(device, "operatingSystem"));
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

    private static IEnumerable<JsonElement> EnumerateDevices(JsonElement payload)
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
            throw new InvalidOperationException("CIPP MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"CIPP MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"CIPP MCP tool error: {errText ?? payload.GetRawText()}");
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

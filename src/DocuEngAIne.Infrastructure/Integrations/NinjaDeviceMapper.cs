using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>ninja_list_devices</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to device DTOs. Live list objects use <c>id</c>, <c>organizationId</c>, <c>locationId</c>,
/// <c>nodeClass</c> (WINDOWS_WORKSTATION|WINDOWS_SERVER|MAC|…), <c>approvalStatus</c>,
/// <c>offline</c>, <c>systemName</c>, <c>dnsName</c> and an <b>optional</b> <c>displayName</c> —
/// most rows have no <c>displayName</c> at all, so the name falls back to
/// <c>systemName</c> then <c>dnsName</c>.
/// List is the sync source — do not call <c>ninja_get_device</c> or <c>ninja_list_devices_detailed</c>.
/// The list has no inactive flag: <c>offline</c> means "not checked in right now" (a sleeping laptop),
/// not "decommissioned", so do not derive skip-inactive from it.
/// Cursor is <c>after</c> = last device id from the previous page, exactly like
/// <see cref="NinjaOrganizationMapper"/>.
/// </summary>
public static class NinjaDeviceMapper
{
    public const string ToolName = "ninja_list_devices";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;

    public static IReadOnlyList<ExternalDeviceDto> MapDevices(string mcpBody)
        => MapDevices(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalDeviceDto> MapDevices(string mcpBody, out int? lastDeviceId)
        => MapDevices(mcpBody, out lastDeviceId, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows the vendor actually returned,
    /// which is NOT the number mapped — rows missing an id, an organizationId, or every name field are
    /// dropped. Paging must be decided on the raw count, or one unmappable device ends the pull.
    /// </summary>
    public static IReadOnlyList<ExternalDeviceDto> MapDevices(string mcpBody, out int? lastDeviceId, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var devices = new List<ExternalDeviceDto>();
        lastDeviceId = null;
        rowCount = 0;
        foreach (var device in EnumerateDevices(payload))
        {
            rowCount++;
            if (TryReadId(device, out var id))
                lastDeviceId = id;
            var mapped = MapDevice(device);
            if (mapped is not null)
                devices.Add(mapped);
        }
        return devices;
    }

    public static string BuildArgumentsJson(int? afterDeviceId, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["pageSize"] = size,
        };
        if (afterDeviceId is int after)
            args["after"] = after;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalDeviceDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var devices = new List<ExternalDeviceDto>();
        int? after = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildArgumentsJson(after, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapDevices(body, out var lastId, out var rowCount);
            devices.AddRange(mapped);
            // Terminate on the rows the vendor returned, never on the rows that mapped. A single
            // device with no displayName/systemName/dnsName is dropped by MapDevice, and testing
            // mapped.Count here would read that short page as the last one and silently abandon
            // every remaining device -- while still reporting the run Succeeded.
            if (rowCount == 0 || rowCount < size)
                break;
            if (lastId is null)
                break;
            after = lastId;
        }
        return devices;
    }

    private static ExternalDeviceDto? MapDevice(JsonElement device)
    {
        if (device.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(device, "id");
        var organizationId = ReadString(device, "organizationId");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(organizationId))
            return null;

        var systemName = ReadString(device, "systemName");
        var dnsName = ReadString(device, "dnsName");
        var name = ReadString(device, "displayName") ?? systemName ?? dnsName;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalDeviceDto(
            ExternalId: id,
            OrganizationExternalId: organizationId,
            Name: name.Trim(),
            NodeClass: ReadString(device, "nodeClass"),
            SystemName: systemName,
            DnsName: dnsName);
    }

    private static bool TryReadId(JsonElement device, out int id)
    {
        id = 0;
        if (device.ValueKind != JsonValueKind.Object || !TryGetProperty(device, out var value, "id"))
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

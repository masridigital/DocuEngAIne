using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>unifi_sm_list_devices</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to device DTOs. This is the account-wide inventory — do not call
/// <c>unifi_net_list_devices</c> (one site, action-tool ids, can restart/adopt).
/// Live list is a wrapper <c>{ "data": [ hostGroup, ... ], nextToken? }</c> plus
/// <c>httpStatusCode</c>/<c>traceId</c>. Each group has <c>hostId</c>, <c>hostName</c>,
/// and <c>devices</c> whose entries use <c>id</c>, <c>mac</c>, <c>name</c>, <c>model</c>,
/// <c>ip</c>, <c>productLine</c>, <c>status</c>, <c>version</c>, <c>isConsole</c>,
/// <c>isManaged</c>.
/// <c>ExternalId</c> is device <c>id</c>; <c>Name</c> is <c>name</c> falling back to
/// <c>mac</c>; <c>OrganizationExternalId</c> is the group's <c>hostId</c> (the same id
/// <see cref="UnifiHostMapper"/> maps to a company). Skip rows missing <c>id</c> or
/// <c>hostId</c>. Do not ingest firmware blobs (<c>firmware</c>, <c>firmwareStatus</c>,
/// <c>updateAvailable</c>, or a huge <c>version</c> payload).
/// Optional Compact <c>hostIds</c> filter is unused on the default pull.
/// Compact <c>pageSize</c> caps at 200. Cursor is wrapper <c>nextToken</c>.
/// </summary>
public static class UnifiDeviceMapper
{
    public const string ToolName = "unifi_sm_list_devices";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static IReadOnlyList<ExternalDeviceDto> MapDevices(string mcpBody)
        => MapDevices(mcpBody, out _, out _);

    public static IReadOnlyList<ExternalDeviceDto> MapDevices(string mcpBody, out string? nextToken)
        => MapDevices(mcpBody, out nextToken, out _);

    /// <summary>
    /// Maps one page. <paramref name="dataCount"/> is the number of host groups the vendor
    /// returned in <c>data</c>, which is NOT the number of devices mapped — groups or devices
    /// missing a required field are dropped. Paging must turn on the raw group count, or one
    /// unmappable host ends the pull and the run still reports Succeeded.
    /// </summary>
    public static IReadOnlyList<ExternalDeviceDto> MapDevices(string mcpBody, out string? nextToken, out int dataCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        nextToken = ReadNextToken(payload);
        var devices = new List<ExternalDeviceDto>();
        dataCount = 0;
        foreach (var host in EnumerateHostGroups(payload))
        {
            dataCount++;
            var hostId = ReadString(host, "hostId");
            foreach (var device in EnumerateDevices(host))
            {
                var mapped = MapDevice(device, hostId);
                if (mapped is not null)
                    devices.Add(mapped);
            }
        }
        return devices;
    }

    public static string BuildArgumentsJson(string? nextToken, int pageSize = DefaultPageSize, string? hostIds = null)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["pageSize"] = size,
        };
        if (!string.IsNullOrWhiteSpace(nextToken))
            args["nextToken"] = nextToken;
        if (!string.IsNullOrWhiteSpace(hostIds))
            args["hostIds"] = hostIds;
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
        string? nextToken = null;
        const int maxPages = 500;
        for (var page = 1; page <= maxPages; page++)
        {
            // Default pull is account-wide: do not send hostIds.
            var args = BuildArgumentsJson(nextToken, size);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var mapped = MapDevices(body, out var pageNextToken, out var dataCount);
            devices.AddRange(mapped);
            // Terminate on the host groups the vendor returned, never on the devices that mapped.
            // A single group with no hostId (or only id-less devices) is dropped by MapDevice,
            // and testing mapped.Count here would read that short page as the last one and
            // silently abandon every remaining device — while still reporting the run Succeeded.
            if (dataCount == 0 || dataCount < size)
                break;
            if (string.IsNullOrWhiteSpace(pageNextToken))
                break;
            nextToken = pageNextToken;
        }
        return devices;
    }

    private static ExternalDeviceDto? MapDevice(JsonElement device, string? hostId)
    {
        if (device.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(device, "id");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(hostId))
            return null;

        var mac = ReadString(device, "mac");
        var name = ReadString(device, "name") ?? mac;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalDeviceDto(
            ExternalId: id,
            OrganizationExternalId: hostId,
            Name: name.Trim(),
            NodeClass: ReadString(device, "productLine"),
            SystemName: mac,
            DnsName: ReadString(device, "ip"));
    }

    private static string? ReadNextToken(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(payload, "nextToken");
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
        // Guarded like the other mappers: a non-object element in the vendor array (a bare string
        // or number row) must read as "property absent", not throw and abort the whole pull.
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

    private static IEnumerable<JsonElement> EnumerateHostGroups(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var data, "data")
            && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray();

        return [];
    }

    private static IEnumerable<JsonElement> EnumerateDevices(JsonElement host)
    {
        if (host.ValueKind == JsonValueKind.Object
            && TryGetProperty(host, out var devices, "devices")
            && devices.ValueKind == JsonValueKind.Array)
            return devices.EnumerateArray();

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
            throw new InvalidOperationException("UniFi MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"UniFi MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"UniFi MCP tool error: {errText ?? payload.GetRawText()}");
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

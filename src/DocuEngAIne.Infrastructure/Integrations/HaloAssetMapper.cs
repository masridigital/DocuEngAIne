using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Halo asset row. <see cref="IsInactive"/> is Halo <c>inactive</c> when present.
/// <see cref="ExternalDeviceDto"/> has no matching property, so the flag lives here
/// until a later sync slice can honour <c>SkipInactive</c>.
/// </summary>
public sealed record HaloAssetDto(
    string ExternalId,
    string OrganizationExternalId,
    string Name,
    bool? IsInactive = null,
    string? NodeClass = null,
    string? SystemName = null,
    string? DnsName = null)
    : ExternalDeviceDto(ExternalId, OrganizationExternalId, Name, NodeClass, SystemName, DnsName);

/// <summary>
/// Maps StackJack Compact <c>halo_list_assets</c> JSON (vendor passthrough, often JSON-RPC wrapped)
/// to device DTOs. Live list objects use <c>id</c>, <c>inventory_number</c> (fallback <c>name</c>),
/// <c>client_id</c>, and <c>inactive</c> when present. Envelope is raw Halo
/// <c>{ record_count, assets }</c>. List is the sync source — do not call
/// <c>halo_get_asset</c> or <c>halo_search_assets</c>.
/// Compact <c>pageNo</c> defaults to 1; <c>pageSize</c> defaults to 50 (max 200).
/// Do not send <c>activeInactive</c> — inactive rows are mapped, not filtered; <c>SkipInactive</c>
/// is honoured later in sync, not here. Page on raw row count, never mapped count.
/// Name clobber policy (<c>AutoUpdateAssetNames</c>) is sync, not this mapper.
/// </summary>
public static class HaloAssetMapper
{
    public const string ToolName = "halo_list_assets";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static IReadOnlyList<HaloAssetDto> MapAssets(string mcpBody)
        => MapAssets(mcpBody, out _);

    /// <summary>
    /// Maps one page. <paramref name="rowCount"/> is the number of rows Halo returned, which is NOT the
    /// number mapped — an asset with no id, no name (inventory_number or name), or no client_id is
    /// dropped. Paging must turn on the raw count, or one unmappable asset ends the pull and the run
    /// still reports Succeeded. <c>record_count</c> is the Halo total, not the page length.
    /// </summary>
    public static IReadOnlyList<HaloAssetDto> MapAssets(string mcpBody, out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var devices = new List<HaloAssetDto>();
        rowCount = 0;
        foreach (var asset in EnumerateAssets(payload))
        {
            rowCount++;
            var mapped = MapAsset(asset);
            if (mapped is not null)
                devices.Add(mapped);
        }
        return devices;
    }

    public static string BuildArgumentsJson(int pageNo, int pageSize = DefaultPageSize, int? clientId = null)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var args = new Dictionary<string, object?>
        {
            ["pageNo"] = pageNo < 1 ? 1 : pageNo,
            ["pageSize"] = size,
        };
        if (clientId is int id)
            args["clientId"] = id;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<HaloAssetDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        int pageSize = DefaultPageSize,
        int? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var devices = new List<HaloAssetDto>();
        const int maxPages = 500;
        for (var pageNo = 1; pageNo <= maxPages; pageNo++)
        {
            var args = BuildArgumentsJson(pageNo, size, clientId);
            var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
            var page = MapAssets(body, out var rowCount);
            devices.AddRange(page);
            // Raw rows, never mapped rows: an asset with no id or name is dropped, and testing the
            // mapped count would read that short page as the last one and abandon the rest.
            if (rowCount < size)
                break;
        }
        return devices;
    }

    private static HaloAssetDto? MapAsset(JsonElement asset)
    {
        if (asset.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(asset, "id", "assetid", "asset_id", "assetId");
        var clientId = ReadString(asset, "client_id", "clientid", "clientId");
        var name = ReadString(asset, "inventory_number", "inventorynumber", "inventoryNumber")
            ?? ReadString(asset, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(name))
            return null;

        return new HaloAssetDto(
            ExternalId: id,
            OrganizationExternalId: clientId,
            Name: name.Trim(),
            IsInactive: ReadInactive(asset));
    }

    /// <summary><c>inactive</c> when present; do not invent a value when the field is missing.</summary>
    private static bool? ReadInactive(JsonElement asset)
    {
        if (TryGetProperty(asset, out var inactive, "inactive", "isinactive", "is_inactive", "isInactive"))
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

    private static IEnumerable<JsonElement> EnumerateAssets(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind != JsonValueKind.Object)
            return [];

        foreach (var name in new[] { "assets", "items", "data", "records", "value", "results" })
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

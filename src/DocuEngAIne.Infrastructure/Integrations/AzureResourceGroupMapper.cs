using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>azure_list_resource_groups</c> JSON (vendor passthrough, often
/// JSON-RPC wrapped) to <see cref="ExternalAzureResourceGroupDto"/>. Compact requires
/// <c>subscriptionId</c> (from <c>azure_list_subscriptions</c>) and returns ARM's
/// <c>{value:[...], nextLink?}</c> envelope (a bare array is also accepted). Each item uses
/// <c>id</c>, <c>name</c>, <c>location</c>, and <c>properties.provisioningState</c>.
/// Skip rows missing a name or a resolvable subscription id. Ignore <c>tags</c> and
/// <c>managedBy</c>. Do not call <c>azure_list_resource_group_resources</c> or ARM/Intune
/// device lists — this mapper is groups only.
/// Compact schema: required <c>subscriptionId</c>, optional <c>entraTenant</c>, <c>filter</c>,
/// <c>maxItems</c> (1–1000, default 1000). Compact already follows ARM <c>nextLink</c>
/// internally up to 1000 items; leftover <c>nextLink</c> is exposed, not re-fed. List is one shot.
/// </summary>
public static class AzureResourceGroupMapper
{
    public const string ToolName = "azure_list_resource_groups";
    public const int DefaultMaxItems = 1000;
    public const int MaxItemsCap = 1000;

    public static IReadOnlyList<ExternalAzureResourceGroupDto> MapResourceGroups(string mcpBody)
        => MapResourceGroups(mcpBody, subscriptionId: null, out _, out _);

    /// <summary>
    /// Maps one Compact/ARM page. <paramref name="rowCount"/> is the number of rows in
    /// <c>value</c>, which is NOT the number mapped — rows missing a required field are dropped.
    /// <paramref name="subscriptionId"/> fills <c>SubscriptionExternalId</c> when a row's ARM
    /// id does not carry it.
    /// </summary>
    public static IReadOnlyList<ExternalAzureResourceGroupDto> MapResourceGroups(
        string mcpBody,
        string? subscriptionId,
        out string? nextLink,
        out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        nextLink = ReadNextLink(payload);
        var groups = new List<ExternalAzureResourceGroupDto>();
        rowCount = 0;
        foreach (var group in EnumerateResourceGroups(payload))
        {
            rowCount++;
            var mapped = MapResourceGroup(group, subscriptionId);
            if (mapped is not null)
                groups.Add(mapped);
        }
        return groups;
    }

    public static string BuildArgumentsJson(
        string subscriptionId,
        string? entraTenant = null,
        string? filter = null,
        int maxItems = DefaultMaxItems)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            throw new ArgumentException("subscriptionId is required.", nameof(subscriptionId));

        var args = new Dictionary<string, object?>
        {
            ["subscriptionId"] = subscriptionId,
            ["maxItems"] = Math.Clamp(maxItems, 1, MaxItemsCap),
        };
        if (!string.IsNullOrWhiteSpace(entraTenant))
            args["entraTenant"] = entraTenant;
        if (!string.IsNullOrWhiteSpace(filter))
            args["filter"] = filter;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalAzureResourceGroupDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        string subscriptionId,
        string? entraTenant = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            throw new ArgumentException("subscriptionId is required.", nameof(subscriptionId));

        var args = BuildArgumentsJson(subscriptionId, entraTenant);
        var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
        return MapResourceGroups(body, subscriptionId, out _, out _);
    }

    private static ExternalAzureResourceGroupDto? MapResourceGroup(JsonElement group, string? fallbackSubscriptionId)
    {
        if (group.ValueKind != JsonValueKind.Object)
            return null;

        var name = ReadString(group, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var armId = ReadString(group, "id");
        var subscriptionId = AzureSubscriptionMapper.SubscriptionIdFromArmId(armId)
            ?? fallbackSubscriptionId;
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return null;

        var externalId = armId ?? name;
        return new ExternalAzureResourceGroupDto(
            ExternalId: externalId,
            SubscriptionExternalId: subscriptionId,
            Name: name.Trim(),
            Location: ReadString(group, "location"),
            ProvisioningState: ReadProvisioningState(group));
    }

    private static string? ReadProvisioningState(JsonElement group)
    {
        if (!TryGetProperty(group, out var properties, "properties")
            || properties.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(properties, "provisioningState");
    }

    private static string? ReadNextLink(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(payload, "nextLink");
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

    private static IEnumerable<JsonElement> EnumerateResourceGroups(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var value, "value")
            && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray();
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
            throw new InvalidOperationException("Azure MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Azure MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Azure MCP tool error: {errText ?? payload.GetRawText()}");
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

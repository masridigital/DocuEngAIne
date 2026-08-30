using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>azure_list_subscriptions</c> JSON (vendor passthrough, often JSON-RPC
/// wrapped) to <see cref="ExternalAzureSubscriptionDto"/>. Compact returns ARM's
/// <c>{value:[...], nextLink?}</c> envelope (a bare array is also accepted). Each item uses
/// <c>subscriptionId</c>, <c>displayName</c>, <c>state</c> (Enabled | Warned | PastDue | Disabled |
/// Deleted), and optional <c>tenantId</c>.
/// Skip rows missing an id or displayName. Skip <c>Disabled</c> and <c>Deleted</c>. Map
/// <c>Warned</c> and <c>PastDue</c> with <c>IsInactive</c> true; <c>Enabled</c> is false.
/// Ignore <c>subscriptionPolicies</c> and <c>authorizationSource</c>. Do not call
/// <c>azure_list_virtual_machines</c>, Intune device lists, or other ARM inventory tools —
/// subscriptions are the list, not a door into bulk device import.
/// Compact schema has optional <c>entraTenant</c> only (no page cursor). Compact already follows
/// ARM <c>nextLink</c> internally up to 1000 items; leftover <c>nextLink</c> is exposed, not
/// re-fed. List is one shot.
/// </summary>
public static class AzureSubscriptionMapper
{
    public const string ToolName = "azure_list_subscriptions";

    public static IReadOnlyList<ExternalAzureSubscriptionDto> MapSubscriptions(string mcpBody)
        => MapSubscriptions(mcpBody, out _, out _);

    /// <summary>
    /// Maps one Compact/ARM page. <paramref name="rowCount"/> is the number of rows in
    /// <c>value</c>, which is NOT the number mapped — Disabled/Deleted and rows missing a
    /// required field are dropped.
    /// </summary>
    public static IReadOnlyList<ExternalAzureSubscriptionDto> MapSubscriptions(
        string mcpBody,
        out string? nextLink,
        out int rowCount)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        nextLink = ReadNextLink(payload);
        var subscriptions = new List<ExternalAzureSubscriptionDto>();
        rowCount = 0;
        foreach (var subscription in EnumerateSubscriptions(payload))
        {
            rowCount++;
            var mapped = MapSubscription(subscription);
            if (mapped is not null)
                subscriptions.Add(mapped);
        }
        return subscriptions;
    }

    public static string BuildArgumentsJson(string? entraTenant = null)
    {
        var args = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(entraTenant))
            args["entraTenant"] = entraTenant;
        return JsonSerializer.Serialize(args);
    }

    public static async Task<IReadOnlyList<ExternalAzureSubscriptionDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        string? entraTenant = null,
        CancellationToken cancellationToken = default)
    {
        var args = BuildArgumentsJson(entraTenant);
        var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, args, cancellationToken);
        return MapSubscriptions(body);
    }

    private static ExternalAzureSubscriptionDto? MapSubscription(JsonElement subscription)
    {
        if (subscription.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadSubscriptionId(subscription);
        var name = ReadString(subscription, "displayName");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        var state = ReadString(subscription, "state");
        if (IsDroppedState(state))
            return null;

        return new ExternalAzureSubscriptionDto(
            ExternalId: id,
            Name: name.Trim(),
            State: state,
            TenantId: ReadString(subscription, "tenantId"),
            IsInactive: ReadIsInactive(state));
    }

    private static bool IsDroppedState(string? state)
        => string.Equals(state, "Disabled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(state, "Deleted", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Warned / PastDue are still real subscriptions with a billing problem — map them inactive.
    /// Enabled is active. Any other remaining state is left null rather than invented.
    /// </summary>
    private static bool? ReadIsInactive(string? state)
    {
        if (string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(state, "Warned", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "PastDue", StringComparison.OrdinalIgnoreCase))
            return true;
        return null;
    }

    private static string? ReadSubscriptionId(JsonElement subscription)
    {
        var id = ReadString(subscription, "subscriptionId");
        if (!string.IsNullOrWhiteSpace(id))
            return id;

        return SubscriptionIdFromArmId(ReadString(subscription, "id"));
    }

    internal static string? SubscriptionIdFromArmId(string? armId)
    {
        if (string.IsNullOrWhiteSpace(armId))
            return null;

        const string prefix = "/subscriptions/";
        if (armId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = armId[prefix.Length..];
            var slash = rest.IndexOf('/');
            var guid = slash < 0 ? rest : rest[..slash];
            return string.IsNullOrWhiteSpace(guid) ? null : guid;
        }

        return Guid.TryParse(armId, out _) ? armId : null;
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

    private static IEnumerable<JsonElement> EnumerateSubscriptions(JsonElement payload)
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

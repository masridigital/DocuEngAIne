using System.Text.Json;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Reads the StackJack tier and monthly call allowance for a connector out of
/// <c>stackjack_session_info</c>, so nobody has to type a plan in by hand.
///
/// StackJack meters <em>per connector subscription, not account-wide</em>, and its docs note that an
/// individual subscription can carry a custom allowance — so the reported <c>monthlyCallLimit</c> is
/// authoritative and the tier name is only a fallback. <c>stackjack_*</c> platform tools are free and
/// never draw down an allowance, so calling this costs nothing.
/// </summary>
public static class StackJackPlanDetector
{
    public const string ToolName = "stackjack_session_info";

    /// <summary>Treated as unlimited; StackJack reports Enterprise as <see cref="int.MaxValue"/>.</summary>
    public const int UnlimitedCallLimit = int.MaxValue;

    /// <summary>
    /// StackJack's connector name for one of our providers. These are the names
    /// <c>stackjack_session_info</c> actually returns, which do not all match our enum
    /// (NinjaOne is "NinjaRMM"; Blackpoint's platform is "CompassOne").
    /// </summary>
    public static string? ConnectorName(IntegrationProvider provider) => provider switch
    {
        IntegrationProvider.Halo => "Halo",
        IntegrationProvider.NinjaOne => "NinjaRMM",
        IntegrationProvider.Cipp => "Cipp",
        IntegrationProvider.Meraki => "Meraki",
        IntegrationProvider.UniFi => "UniFi",
        IntegrationProvider.Action1 => "Action1",
        IntegrationProvider.Autotask => "Autotask",
        IntegrationProvider.Blackpoint => "CompassOne",
        _ => null,
    };

    public record ConnectorPlan(string Connector, StackJackPlan Plan, int? MonthlyCallLimit, bool HasCredentials);

    public static StackJackPlan ParsePlan(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "free" => StackJackPlan.Free,
        "pro" => StackJackPlan.Pro,
        "business" => StackJackPlan.Business,
        "enterprise" => StackJackPlan.Enterprise,
        _ => StackJackPlan.Unknown,
    };

    /// <summary>Finds the connector entry for a provider, or null when the session does not carry one.</summary>
    public static ConnectorPlan? FindConnector(string sessionInfoBody, IntegrationProvider provider)
    {
        var connectorName = ConnectorName(provider);
        if (connectorName is null)
            return null;

        foreach (var connector in EnumerateConnectors(sessionInfoBody))
        {
            var name = ReadString(connector, "connector");
            if (!string.Equals(name, connectorName, StringComparison.OrdinalIgnoreCase))
                continue;

            return new ConnectorPlan(
                Connector: name!,
                Plan: ParsePlan(ReadString(connector, "plan")),
                MonthlyCallLimit: ReadInt(connector, "monthlyCallLimit"),
                HasCredentials: ReadBool(connector, "hasCredentials") ?? false);
        }

        return null;
    }

    public static async Task<ConnectorPlan?> DetectAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        IntegrationProvider provider,
        CancellationToken cancellationToken = default)
    {
        var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, null, cancellationToken);
        return FindConnector(body, provider);
    }

    private static IEnumerable<JsonElement> EnumerateConnectors(string body)
    {
        var payload = UnwrapMcpPayload(body);
        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var connectors, "connectors")
            && connectors.ValueKind == JsonValueKind.Array)
        {
            return connectors.EnumerateArray();
        }

        return [];
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static bool? ReadBool(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names))
            return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        return null;
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

    /// <summary>Same JSON-RPC / tool-content unwrapping the connector mappers use.</summary>
    private static JsonElement UnwrapMcpPayload(string mcpBody)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(mcpBody);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("StackJack session_info returned non-JSON.", ex);
        }

        // Surface a tool error instead of returning no connectors: an empty connector list is how a
        // caller learns "this connector is not subscribed", so swallowing an error here would report
        // an auth or transport failure as an unsubscribed connector.
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"StackJack session_info error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException(
                $"StackJack session_info error: {ReadContentText(payload) ?? payload.GetRawText()}");
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
                    // fall through to the structured payload
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
            if (item.ValueKind == JsonValueKind.Object
                && TryGetProperty(item, out var text, "text")
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }

        return null;
    }
}

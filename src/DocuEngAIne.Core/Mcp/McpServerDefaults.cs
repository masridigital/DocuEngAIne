using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Core.Mcp;

public static class McpServerDefaults
{
    /// <summary>The only StackJack endpoint. Origin without /mcp fails auth.</summary>
    public const string StackJackCompactEndpoint = "https://compact.stackjack.io/mcp";

    /// <summary>Composio Connect remote MCP.</summary>
    public const string ComposioEndpoint = "https://connect.composio.dev/mcp";

    /// <summary>Name given to the Compact registration created on demand for a tenant that has none.</summary>
    public const string StackJackCompactName = "StackJack Compact";

    /// <summary>
    /// Providers whose live pulls run through StackJack Compact rather than a vendor REST API. Compact
    /// is built in for these: an admin supplies a Key Vault secret name and the tenant's Compact
    /// registration is resolved — or created once — on their behalf.
    ///
    /// Blackpoint is deliberately absent. It has a Compact connector (CompassOne) but no pull path in
    /// IntegrationSyncService yet, so auto-creating a server for it would register egress nothing uses.
    /// </summary>
    public static bool IsCompactBacked(IntegrationProvider provider) => provider switch
    {
        IntegrationProvider.Halo
            or IntegrationProvider.NinjaOne
            or IntegrationProvider.Cipp
            or IntegrationProvider.Meraki
            or IntegrationProvider.UniFi
            or IntegrationProvider.Action1
            or IntegrationProvider.Autotask => true,
        _ => false,
    };

    public static string EndpointFor(McpServerKind kind) => kind switch
    {
        McpServerKind.StackJackCompact => StackJackCompactEndpoint,
        McpServerKind.Composio => ComposioEndpoint,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown MCP server kind."),
    };

    public static string ResolveEndpoint(McpServerKind kind, string? endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
            return EndpointFor(kind);

        var trimmed = endpointUrl.Trim().TrimEnd('/');
        if (kind == McpServerKind.StackJackCompact
            && (string.Equals(trimmed, "https://compact.stackjack.io", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "http://compact.stackjack.io", StringComparison.OrdinalIgnoreCase)))
        {
            return StackJackCompactEndpoint;
        }

        return endpointUrl.Trim();
    }
}

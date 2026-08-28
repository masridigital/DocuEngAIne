namespace DocuEngAIne.Core.Enums;

/// <summary>
/// First-class MCP providers registered per tenant.
/// StackJack Compact is the only StackJack endpoint (Halo, NinjaOne, CIPP, Meraki, UniFi, …).
/// Composio is the 1000+ app Connect MCP — not a replacement for Compact PSA/RMM connectors.
/// </summary>
public enum McpServerKind
{
    StackJackCompact = 0,
    Composio = 1,
}

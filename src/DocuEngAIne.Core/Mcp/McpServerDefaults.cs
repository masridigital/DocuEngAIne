using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Core.Mcp;

public static class McpServerDefaults
{
    /// <summary>The only StackJack endpoint. Origin without /mcp fails auth.</summary>
    public const string StackJackCompactEndpoint = "https://compact.stackjack.io/mcp";

    /// <summary>Composio Connect remote MCP.</summary>
    public const string ComposioEndpoint = "https://connect.composio.dev/mcp";

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

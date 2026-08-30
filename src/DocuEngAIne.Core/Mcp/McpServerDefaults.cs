using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>Name used when registering the Composio Connect harness.</summary>
    public const string ComposioName = "Composio Connect";

    /// <summary>
    /// Composio apps this product will invoke. The Connect catalog is 1000+ apps; we only take
    /// GitHub, Cloudflare, Outlook, and Notion. Ads and social stay out.
    /// </summary>
    public static readonly string[] AllowedComposioToolkits = ["github", "cloudflare", "outlook", "notion"];

    /// <summary>Ads and social toolkits. Never invoked, even if they appear in a tools/list.</summary>
    public static readonly string[] SkippedComposioToolkits =
        ["googleads", "facebook", "instagram", "linkedin", "reddit"];

    /// <summary>
    /// Providers whose live pulls run through StackJack Compact rather than a vendor REST API. Compact
    /// is built in for these: an admin supplies a Key Vault secret name and the tenant's Compact
    /// registration is resolved — or created once — on their behalf.
    /// </summary>
    public static bool IsCompactBacked(IntegrationProvider provider) => provider switch
    {
        IntegrationProvider.Halo
            or IntegrationProvider.NinjaOne
            or IntegrationProvider.Cipp
            or IntegrationProvider.Meraki
            or IntegrationProvider.UniFi
            or IntegrationProvider.Action1
            or IntegrationProvider.Autotask
            or IntegrationProvider.Blackpoint
            or IntegrationProvider.DefensX
            or IntegrationProvider.Pax8
            or IntegrationProvider.Slide => true,
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

        if (kind == McpServerKind.Composio
            && (string.Equals(trimmed, "https://connect.composio.dev", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "http://connect.composio.dev", StringComparison.OrdinalIgnoreCase)))
        {
            return ComposioEndpoint;
        }

        return endpointUrl.Trim();
    }

    public static bool IsAllowedComposioToolkit(string? toolkit)
    {
        var slug = ToolkitSlug(toolkit);
        if (slug is null)
            return false;
        if (IsSkippedComposioToolkit(slug))
            return false;
        return AllowedComposioToolkits.Contains(slug, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsSkippedComposioToolkit(string? toolkit)
    {
        var slug = ToolkitSlug(toolkit);
        return slug is not null
            && SkippedComposioToolkits.Contains(slug, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Composio tools are <c>TOOLKIT_ACTION</c> (and occasionally <c>toolkit/action</c>).
    /// Meta tools such as <c>COMPOSIO_MULTI_EXECUTE_TOOL</c> are not on the allowlist.
    /// </summary>
    public static bool IsAllowedComposioTool(string? toolName)
        => IsAllowedComposioToolkit(ToolkitFromToolName(toolName));

    public static string? ToolkitFromToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return null;

        var name = toolName.Trim();
        var separator = name.IndexOfAny(['_', '/', ':']);
        return ToolkitSlug(separator < 0 ? name : name[..separator]);
    }

    /// <summary>
    /// Drops ads/social and any other toolkit that is not on <see cref="AllowedComposioToolkits"/>
    /// from a <c>tools/list</c> body. Unparsable payloads are returned unchanged.
    /// </summary>
    public static string FilterComposioToolsList(string body)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }

        if (node is not JsonObject)
            return body;

        var tools = node["result"]?["tools"] as JsonArray ?? node["tools"] as JsonArray;
        if (tools is null)
            return body;

        for (var i = tools.Count - 1; i >= 0; i--)
        {
            var name = tools[i]?["name"]?.GetValue<string>();
            if (!IsAllowedComposioTool(name))
                tools.RemoveAt(i);
        }

        return node.ToJsonString();
    }

    private static string? ToolkitSlug(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

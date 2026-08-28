namespace DocuEngAIne.Core.Enums;

/// <summary>
/// StackJack subscription tier for one connector. StackJack bills and meters
/// <em>per connector subscription, not account-wide</em>, so this lives on
/// <c>IntegrationConnection</c> rather than on the shared <c>McpServer</c> registration.
/// Detected from <c>stackjack_session_info</c>; not something a user should have to type in.
/// </summary>
public enum StackJackPlan
{
    /// <summary>Tier could not be determined (detection never ran, or the connector was absent from the session).</summary>
    Unknown = 0,

    /// <summary>100 successful tool calls per connector, per billing cycle.</summary>
    Free = 1,

    /// <summary>5,000 per cycle (7,500 with the TechTribe perk).</summary>
    Pro = 2,

    /// <summary>50,000 per cycle (75,000 with the TechTribe perk).</summary>
    Business = 3,

    /// <summary>Unlimited.</summary>
    Enterprise = 4,
}

namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// One Azure subscription from Compact <c>azure_list_subscriptions</c>, bound for a later
/// <c>Asset</c> ingest. <c>ExternalId</c> is ARM <c>subscriptionId</c>. <c>State</c> is the
/// vendor string (Enabled | Warned | PastDue | Disabled | Deleted). Disabled and Deleted
/// rows are dropped by the mapper; Warned and PastDue map with <c>IsInactive</c> true.
/// </summary>
public record ExternalAzureSubscriptionDto(
    string ExternalId,
    string Name,
    string? State = null,
    string? TenantId = null,
    bool? IsInactive = null);

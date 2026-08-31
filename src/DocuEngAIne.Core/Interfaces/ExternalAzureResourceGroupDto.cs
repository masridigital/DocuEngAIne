namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// One Azure resource group from Compact <c>azure_list_resource_groups</c>, bound for a later
/// <c>Asset</c> ingest. <c>SubscriptionExternalId</c> is the parent subscription id required
/// by the tool (and present on the ARM <c>id</c>). Do not persist <c>tags</c> or <c>managedBy</c>.
/// </summary>
public record ExternalAzureResourceGroupDto(
    string ExternalId,
    string SubscriptionExternalId,
    string Name,
    string? Location = null,
    string? ProvisioningState = null);

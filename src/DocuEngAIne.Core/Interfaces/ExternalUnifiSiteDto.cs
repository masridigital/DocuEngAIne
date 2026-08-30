namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// One account-wide UniFi Site Manager site from <c>unifi_sm_list_sites</c>
/// (not <c>unifi_net_list_sites</c>). <c>HostExternalId</c> joins to
/// <c>unifi_sm_list_hosts</c>. Do not persist the vendor <c>statistics</c> block.
/// </summary>
public record ExternalUnifiSiteDto(
    string ExternalId,
    string HostExternalId,
    string Name,
    string? Timezone = null);

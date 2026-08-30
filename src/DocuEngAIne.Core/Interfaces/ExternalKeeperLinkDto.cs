namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// One Compact Keeper row bound for a <c>KeeperLink</c>. Compact has no vault record list,
/// so this is never a vault record: <see cref="KeeperRecordUrl"/> is always null, and
/// <see cref="ExternalId"/> is an MSP <c>vendorInternalId</c>/<c>partnerId</c> or a SCIM
/// user id — not a Keeper vault UID. Never carries a password, secret, or token.
/// </summary>
public record ExternalKeeperLinkDto(
    string ExternalId,
    string Name,
    string? UsernameHint = null,
    string? KeeperRecordUrl = null);

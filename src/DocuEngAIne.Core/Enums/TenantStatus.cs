namespace DocuEngAIne.Core.Enums;

/// <summary>Where a tenant is in its lifecycle. Only an <see cref="Active"/> tenant can be used.</summary>
public enum TenantStatus
{
    Active = 0,

    /// <summary>Closed for now by a platform operator (for example, unpaid), with a reason its users see. Reversible.</summary>
    Suspended = 1,

    /// <summary>Closed: nobody can use it and sync stops, but its data is kept and a platform operator can restore it.</summary>
    Archived = 2,
}

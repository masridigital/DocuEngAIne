namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// Soft-deletable entity. A non-null <see cref="DeletedAt"/> means the row is archived: the global
/// query filter hides it from every normal query, the Museum lists it through its
/// <c>ArchiveEntry</c>, and it stays restorable until permanently deleted.
/// </summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}

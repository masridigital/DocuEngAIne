using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>A reusable set of choices for Select and MultiSelect fields, shared across layouts.</summary>
public class OptionList : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>An inactive list accepts no new values; stored values are kept.</summary>
    public bool IsActive { get; set; } = true;

    public ICollection<OptionListItem> Items { get; set; } = [];
}

public class OptionListItem : EntityBase
{
    public Guid OptionListId { get; set; }
    public OptionList OptionList { get; set; } = null!;

    public required string Label { get; set; }

    /// <summary>What asset field values store. Set once from the label and never changed, so renames are safe.</summary>
    public required string Value { get; set; }
    public int SortOrder { get; set; }

    /// <summary>An inactive item can no longer be chosen; values that already hold it are kept.</summary>
    public bool IsActive { get; set; } = true;
}

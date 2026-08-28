using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class DocumentFolder : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }

    public Guid? ParentId { get; set; }
    public DocumentFolder? Parent { get; set; }

    public required string Name { get; set; }

    public ICollection<DocumentFolder> Children { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
}

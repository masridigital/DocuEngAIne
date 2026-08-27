using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Data;

public class DocuEngAIneDbContext : DbContext
{
    private readonly ICurrentUser _currentUser;

    public DocuEngAIneDbContext(DbContextOptions<DocuEngAIneDbContext> options, ICurrentUser currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AssetType> AssetTypes => Set<AssetType>();
    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<AssetDocumentLink> AssetDocumentLinks => Set<AssetDocumentLink>();
    public DbSet<EncryptedSecret> EncryptedSecrets => Set<EncryptedSecret>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyDocuEngAIneConfiguration();
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;
        var userId = _currentUser.IsAuthenticated && !string.IsNullOrEmpty(_currentUser.ObjectId)
            ? await ResolveUserIdAsync(cancellationToken)
            : (Guid?)null;

        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is ITenantScoped scoped && tenantId.HasValue)
                    {
                        scoped.TenantId = tenantId.Value;
                    }
                    entry.Entity.CreatedAt = DateTimeOffset.UtcNow;
                    entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
                    break;
            }
        }

        var result = await base.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<Guid?> ResolveUserIdAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.TenantId is null || _currentUser.ObjectId is null)
            return null;

        var user = await Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == _currentUser.TenantId && u.EntraObjectId == _currentUser.ObjectId, cancellationToken);

        return user?.Id;
    }
}


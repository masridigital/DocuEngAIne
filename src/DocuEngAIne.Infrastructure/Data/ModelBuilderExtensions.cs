using DocuEngAIne.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Data;

public static class ModelBuilderExtensions
{
    public static void ApplyDocuEngAIneConfiguration(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(t =>
        {
            t.Property(x => x.Id).ValueGeneratedNever();
            t.HasIndex(x => x.Slug).IsUnique();
            t.HasIndex(x => x.PrimaryDomain);
            t.HasMany(x => x.Users).WithOne(u => u.Tenant).HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.AssetTypes).WithOne(a => a.Tenant).HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.Assets).WithOne(a => a.Tenant).HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.Documents).WithOne(d => d.Tenant).HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.KeeperLinks).WithOne(s => s.Tenant).HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.Runbooks).WithOne(r => r.Tenant).HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.ResourceRoleAssignments).WithOne(r => r.Tenant).HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(u =>
        {
            u.HasIndex(x => new { x.TenantId, x.EntraObjectId }).IsUnique();
            u.HasIndex(x => x.Email);
        });

        modelBuilder.Entity<AssetType>(a =>
        {
            a.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<FieldDefinition>(f =>
        {
            f.HasIndex(x => new { x.AssetTypeId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<Asset>(a =>
        {
            a.HasIndex(x => x.AssetTypeId);
            a.HasIndex(x => new { x.TenantId, x.Name });
            a.HasMany(x => x.CustomFieldValues).WithOne(v => v.Asset).HasForeignKey(v => v.AssetId).OnDelete(DeleteBehavior.Cascade);
            a.HasMany(x => x.Documents).WithOne(l => l.Asset).HasForeignKey(l => l.AssetId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomFieldValue>(v =>
        {
            v.HasIndex(x => new { x.AssetId, x.FieldDefinitionId }).IsUnique();
        });

        modelBuilder.Entity<Document>(d =>
        {
            d.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
            d.HasIndex(x => x.Tags);
            d.HasMany(x => x.LinkedAssets).WithOne(l => l.Document).HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);
            d.HasMany(x => x.Versions).WithOne(v => v.Document).HasForeignKey(v => v.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DocumentVersion>(v =>
        {
            v.HasIndex(x => new { x.DocumentId, x.VersionNumber });
        });

        modelBuilder.Entity<AssetDocumentLink>(l =>
        {
            l.HasIndex(x => new { x.AssetId, x.DocumentId }).IsUnique();
        });

        modelBuilder.Entity<KeeperLink>(k =>
        {
            k.HasIndex(x => new { x.TenantId, x.Name });
        });

        modelBuilder.Entity<Runbook>(r =>
        {
            r.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
            r.HasMany(x => x.Steps).WithOne(s => s.Runbook).HasForeignKey(s => s.RunbookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunbookStep>(s =>
        {
            s.HasIndex(x => new { x.RunbookId, x.Order }).IsUnique();
        });

        modelBuilder.Entity<ResourceRoleAssignment>(r =>
        {
            r.HasIndex(x => new { x.TenantId, x.UserId, x.ResourceType, x.ResourceId }).IsUnique();
        });

        modelBuilder.Entity<AuditLog>(a =>
        {
            a.HasIndex(x => x.TenantId);
            a.HasIndex(x => x.Action);
            a.HasIndex(x => x.CreatedAt);
        });
    }
}

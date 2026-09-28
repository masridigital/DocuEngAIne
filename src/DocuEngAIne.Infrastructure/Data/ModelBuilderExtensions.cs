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
            t.HasMany(x => x.Companies).WithOne(c => c.Tenant).HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.AssetTypes).WithOne(a => a.Tenant).HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: AssetType already cascades from Tenant — SQL Server forbids multiple cascade paths.
            t.HasMany(x => x.Assets).WithOne(a => a.Tenant).HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            t.HasMany(x => x.Documents).WithOne(d => d.Tenant).HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.DocumentFolders).WithOne(f => f.Tenant).HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.KeeperLinks).WithOne(s => s.Tenant).HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.Runbooks).WithOne(r => r.Tenant).HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: User already cascades from Tenant — SQL Server forbids multiple cascade paths.
            t.HasMany(x => x.ResourceRoleAssignments).WithOne(r => r.Tenant).HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
            t.HasMany(x => x.McpServers).WithOne(m => m.Tenant).HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
            t.HasMany(x => x.IntegrationConnections).WithOne(i => i.Tenant).HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
            t.HasMany(x => x.FlagDefinitions).WithOne(f => f.Tenant).HasForeignKey(f => f.TenantId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: Tenant already cascades to Users / Companies / … — SQL Server forbids another cascade path.
            t.HasMany(x => x.ApiTokens).WithOne(a => a.Tenant).HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Company>(c =>
        {
            c.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
            c.HasIndex(x => new { x.TenantId, x.HaloClientId });
            c.HasIndex(x => new { x.TenantId, x.NinjaOrganizationId });
            c.HasIndex(x => x.ParentCompanyId);
            // Restrict: Tenant already cascades to Company — SQL Server forbids multiple cascade paths on the self-FK.
            c.HasOne(x => x.ParentCompany).WithMany(x => x.ChildCompanies).HasForeignKey(x => x.ParentCompanyId).OnDelete(DeleteBehavior.Restrict);
            // Restrict: Company and Tenant/AssetType both reach these tables — SQL Server forbids multiple cascade paths (including SET NULL).
            c.HasMany(x => x.Assets).WithOne(a => a.Company).HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
            c.HasMany(x => x.Documents).WithOne(d => d.Company).HasForeignKey(d => d.CompanyId).OnDelete(DeleteBehavior.Restrict);
            c.HasMany(x => x.DocumentFolders).WithOne(f => f.Company).HasForeignKey(f => f.CompanyId).OnDelete(DeleteBehavior.Restrict);
            c.HasMany(x => x.Runbooks).WithOne(r => r.Company).HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Restrict);
            c.HasMany(x => x.KeeperLinks).WithOne(k => k.Company).HasForeignKey(k => k.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<McpServer>(m =>
        {
            m.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            m.Property(x => x.Kind).HasDefaultValue(DocuEngAIne.Core.Enums.McpServerKind.StackJackCompact);
        });

        modelBuilder.Entity<IntegrationConnection>(i =>
        {
            i.HasIndex(x => new { x.TenantId, x.Provider, x.DisplayName });
            i.Property(x => x.SkipInactive).HasDefaultValue(true);
            i.Property(x => x.SkipContacts).HasDefaultValue(false);
            i.Property(x => x.SkipLocations).HasDefaultValue(false);
            i.Property(x => x.SkipAssets).HasDefaultValue(false);
            i.Property(x => x.AutoUpdateAssetNames).HasDefaultValue(false);
            i.Property(x => x.UpdateCompanyDetails).HasDefaultValue(false);
            i.HasOne(x => x.McpServer).WithMany(m => m.Integrations).HasForeignKey(x => x.McpServerId).OnDelete(DeleteBehavior.SetNull);
            i.HasMany(x => x.Mappings).WithOne(m => m.IntegrationConnection).HasForeignKey(m => m.IntegrationConnectionId).OnDelete(DeleteBehavior.Cascade);
            i.HasMany(x => x.SyncRuns).WithOne(s => s.IntegrationConnection).HasForeignKey(s => s.IntegrationConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IntegrationMapping>(m =>
        {
            m.HasIndex(x => new { x.IntegrationConnectionId, x.ExternalType, x.ExternalId }).IsUnique();
            m.HasIndex(x => new { x.TenantId, x.LocalEntityType, x.LocalEntityId });
            // Restrict: IntegrationConnection already cascades from Tenant — SQL Server forbids multiple cascade paths.
            m.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SyncRun>(s =>
        {
            s.HasIndex(x => new { x.IntegrationConnectionId, x.StartedAt });
            s.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
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
            f.Property(x => x.IsExpiration).HasDefaultValue(false);
        });

        modelBuilder.Entity<Asset>(a =>
        {
            a.HasIndex(x => x.AssetTypeId);
            a.HasIndex(x => new { x.TenantId, x.Name });
            a.HasIndex(x => x.ExpiresAt);
            a.HasMany(x => x.CustomFieldValues).WithOne(v => v.Asset).HasForeignKey(v => v.AssetId).OnDelete(DeleteBehavior.Cascade);
            a.HasMany(x => x.Documents).WithOne(l => l.Asset).HasForeignKey(l => l.AssetId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomFieldValue>(v =>
        {
            v.HasIndex(x => new { x.AssetId, x.FieldDefinitionId }).IsUnique();
            // Restrict: Asset already cascades to CustomFieldValue via AssetType — SQL Server forbids multiple cascade paths.
            v.HasOne(x => x.FieldDefinition).WithMany().HasForeignKey(x => x.FieldDefinitionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DocumentFolder>(f =>
        {
            f.HasIndex(x => new { x.TenantId, x.Name });
            f.HasIndex(x => new { x.TenantId, x.CompanyId });
            f.HasIndex(x => x.ParentId);
            f.Property(x => x.Name).HasMaxLength(450);
            // Restrict: Tenant already cascades to DocumentFolders — SQL Server forbids a second cascade on the self-ref.
            f.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Document>(d =>
        {
            d.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
            d.HasIndex(x => x.Tags);
            d.HasIndex(x => x.FolderId);
            // Restrict: Asset already cascades to AssetDocumentLink — SQL Server forbids multiple cascade paths.
            d.HasMany(x => x.LinkedAssets).WithOne(l => l.Document).HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Restrict);
            d.HasMany(x => x.Versions).WithOne(v => v.Document).HasForeignKey(v => v.DocumentId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: Tenant already cascades to Documents and DocumentFolders — SQL Server forbids multiple cascade paths.
            d.HasOne(x => x.Folder).WithMany(f => f.Documents).HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
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
            r.HasMany(x => x.Runs).WithOne(s => s.Runbook).HasForeignKey(s => s.RunbookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunbookStep>(s =>
        {
            s.HasIndex(x => new { x.RunbookId, x.Order }).IsUnique();
        });

        modelBuilder.Entity<RunbookRun>(r =>
        {
            r.HasIndex(x => new { x.RunbookId, x.StartedAt });
            r.HasIndex(x => new { x.TenantId, x.StartedAt });
            // Restrict: Runbook already cascades from Tenant — SQL Server forbids multiple cascade paths.
            r.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            // Restrict: Company already SetNull-cascades to Runbook — avoid a second path to RunbookRun.
            r.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ResourceRoleAssignment>(r =>
        {
            r.HasIndex(x => new { x.TenantId, x.UserId, x.ResourceType, x.ResourceId }).IsUnique();
        });


        modelBuilder.Entity<FlagDefinition>(f =>
        {
            f.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            f.Property(x => x.Color).HasMaxLength(16);
            f.Property(x => x.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<FlagAssignment>(a =>
        {
            a.HasIndex(x => new { x.TenantId, x.FlagDefinitionId, x.EntityType, x.EntityId }).IsUnique();
            a.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            a.HasIndex(x => new { x.TenantId, x.CreatedAt });
            a.HasOne(x => x.FlagDefinition).WithMany(d => d.Assignments).HasForeignKey(x => x.FlagDefinitionId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: FlagDefinition already cascades from Tenant — SQL Server forbids multiple cascade paths.
            a.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ResourceLink>(l =>
        {
            l.Property(x => x.FromType).HasMaxLength(32);
            l.Property(x => x.ToType).HasMaxLength(32);
            l.Property(x => x.Label).HasMaxLength(256);
            l.HasIndex(x => new { x.TenantId, x.FromType, x.FromId, x.ToType, x.ToId }).IsUnique();
            l.HasIndex(x => new { x.TenantId, x.ToType, x.ToId });
            l.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(a =>
        {
            a.Property(x => x.ActorObjectId).HasMaxLength(128);
            a.HasIndex(x => x.TenantId);
            a.HasIndex(x => x.Action);
            a.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<ApiToken>(a =>
        {
            a.Property(x => x.Name).HasMaxLength(200);
            a.Property(x => x.TokenHash).HasMaxLength(64);
            a.Property(x => x.TokenPrefix).HasMaxLength(16);
            a.Property(x => x.CreatedByObjectId).HasMaxLength(128);
            a.HasIndex(x => x.TokenHash).IsUnique();
            a.HasIndex(x => new { x.TenantId, x.Name });
        });
    }
}

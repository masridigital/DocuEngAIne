using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Data;

public class DocuEngAIneDbContext : DbContext
{
    /// <summary>
    /// Name of the per-request company scope filter on every company-owned entity (see
    /// <see cref="ICompanyScopeAccessor"/>). Ignore it only in code that must see every company,
    /// such as resolving the scope itself.
    /// </summary>
    public const string CompanyScopeFilter = "CompanyScope";

    private readonly ICurrentUser _currentUser;
    private readonly ICompanyScopeAccessor? _companyScope;

    public DocuEngAIneDbContext(
        DbContextOptions<DocuEngAIneDbContext> options,
        ICurrentUser currentUser,
        ICompanyScopeAccessor? companyScope = null)
        : base(options)
    {
        _currentUser = currentUser;
        _companyScope = companyScope;
    }

    /// <summary>
    /// The scope this context's company filters apply. Write guards read it here so a check and the
    /// query filters can never disagree about what the caller may touch.
    /// </summary>
    public CompanyAccessScope CompanyScope => Scope;

    // Read by the query filters on every query; EF binds them from the executing context instance.
    private CompanyAccessScope Scope => _companyScope?.Current ?? CompanyAccessScope.Unrestricted;
    private bool ScopeUnrestricted => Scope.IsUnrestricted;
    private bool ScopeIncludesTenantWide => Scope.IncludesTenantWide;
    private List<Guid> ScopeCompanyIds => Scope.CompanyIds;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<McpServer> McpServers => Set<McpServer>();
    public DbSet<IntegrationConnection> IntegrationConnections => Set<IntegrationConnection>();
    public DbSet<IntegrationMapping> IntegrationMappings => Set<IntegrationMapping>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<AssetType> AssetTypes => Set<AssetType>();
    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentFolder> DocumentFolders => Set<DocumentFolder>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<AssetDocumentLink> AssetDocumentLinks => Set<AssetDocumentLink>();
    public DbSet<KeeperLink> KeeperLinks => Set<KeeperLink>();
    public DbSet<Runbook> Runbooks => Set<Runbook>();
    public DbSet<RunbookStep> RunbookSteps => Set<RunbookStep>();
    public DbSet<RunbookRun> RunbookRuns => Set<RunbookRun>();
    public DbSet<ResourceRoleAssignment> ResourceRoleAssignments => Set<ResourceRoleAssignment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<FlagDefinition> FlagDefinitions => Set<FlagDefinition>();
    public DbSet<FlagAssignment> FlagAssignments => Set<FlagAssignment>();
    public DbSet<ResourceLink> ResourceLinks => Set<ResourceLink>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<ArchiveEntry> ArchiveEntries => Set<ArchiveEntry>();
    public DbSet<AccessReview> AccessReviews => Set<AccessReview>();
    public DbSet<AccessReviewItem> AccessReviewItems => Set<AccessReviewItem>();
    public DbSet<IpAllowlistEntry> IpAllowlistEntries => Set<IpAllowlistEntry>();
    public DbSet<TenantFeatureSetting> TenantFeatureSettings => Set<TenantFeatureSetting>();
    public DbSet<SecurityGroup> SecurityGroups => Set<SecurityGroup>();
    public DbSet<SecurityGroupMember> SecurityGroupMembers => Set<SecurityGroupMember>();
    public DbSet<SecurityGroupCompanyGrant> SecurityGroupCompanyGrants => Set<SecurityGroupCompanyGrant>();
    public DbSet<AssetTypeCompanyActivation> AssetTypeCompanyActivations => Set<AssetTypeCompanyActivation>();
    public DbSet<AssetTypeVersion> AssetTypeVersions => Set<AssetTypeVersion>();
    public DbSet<OptionList> OptionLists => Set<OptionList>();
    public DbSet<OptionListItem> OptionListItems => Set<OptionListItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyDocuEngAIneConfiguration();
        ApplyCompanyScopeFilters(modelBuilder);
    }

    /// <summary>
    /// Company scoping as named query filters, so it covers every query — lists, rollups, joins,
    /// portal, MCP — without each call site opting in. Defined here rather than with the rest of
    /// the model because the filters read this context's scope. A record with no company is
    /// tenant-wide: visible to a restricted caller only when their scope includes tenant-wide.
    /// </summary>
    private void ApplyCompanyScopeFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Company>().HasQueryFilter(CompanyScopeFilter,
            c => ScopeUnrestricted || ScopeCompanyIds.Contains(c.Id));

        modelBuilder.Entity<Asset>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.CompanyId == null && ScopeIncludesTenantWide)
                || (x.CompanyId != null && ScopeCompanyIds.Contains(x.CompanyId.Value)));
        modelBuilder.Entity<Document>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.CompanyId == null && ScopeIncludesTenantWide)
                || (x.CompanyId != null && ScopeCompanyIds.Contains(x.CompanyId.Value)));
        modelBuilder.Entity<DocumentFolder>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.CompanyId == null && ScopeIncludesTenantWide)
                || (x.CompanyId != null && ScopeCompanyIds.Contains(x.CompanyId.Value)));
        modelBuilder.Entity<Runbook>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.CompanyId == null && ScopeIncludesTenantWide)
                || (x.CompanyId != null && ScopeCompanyIds.Contains(x.CompanyId.Value)));
        modelBuilder.Entity<RunbookRun>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.CompanyId == null && ScopeIncludesTenantWide)
                || (x.CompanyId != null && ScopeCompanyIds.Contains(x.CompanyId.Value)));
        modelBuilder.Entity<KeeperLink>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.CompanyId == null && ScopeIncludesTenantWide)
                || (x.CompanyId != null && ScopeCompanyIds.Contains(x.CompanyId.Value)));
        modelBuilder.Entity<ArchiveEntry>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.CompanyId == null && ScopeIncludesTenantWide)
                || (x.CompanyId != null && ScopeCompanyIds.Contains(x.CompanyId.Value)));

        // Dependents follow their owner, so a direct query on them cannot reach around the owner's filter.
        modelBuilder.Entity<CustomFieldValue>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.Asset.CompanyId == null && ScopeIncludesTenantWide)
                || (x.Asset.CompanyId != null && ScopeCompanyIds.Contains(x.Asset.CompanyId.Value)));
        modelBuilder.Entity<DocumentVersion>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.Document.CompanyId == null && ScopeIncludesTenantWide)
                || (x.Document.CompanyId != null && ScopeCompanyIds.Contains(x.Document.CompanyId.Value)));
        modelBuilder.Entity<RunbookStep>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (x.Runbook.CompanyId == null && ScopeIncludesTenantWide)
                || (x.Runbook.CompanyId != null && ScopeCompanyIds.Contains(x.Runbook.CompanyId.Value)));
        modelBuilder.Entity<AssetDocumentLink>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted
                || (((x.Asset.CompanyId == null && ScopeIncludesTenantWide)
                        || (x.Asset.CompanyId != null && ScopeCompanyIds.Contains(x.Asset.CompanyId.Value)))
                    && ((x.Document.CompanyId == null && ScopeIncludesTenantWide)
                        || (x.Document.CompanyId != null && ScopeCompanyIds.Contains(x.Document.CompanyId.Value)))));
        modelBuilder.Entity<SecurityGroupCompanyGrant>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted || ScopeCompanyIds.Contains(x.CompanyId));
        modelBuilder.Entity<AssetTypeCompanyActivation>().HasQueryFilter(CompanyScopeFilter,
            x => ScopeUnrestricted || ScopeCompanyIds.Contains(x.CompanyId));
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

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
}


using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Halo, NinjaOne, CIPP, Meraki and UniFi each own their own mapping rows. Without a match step
/// the same client is created once per connection. These cover the convergence path.
/// </summary>
public class CompanyConvergenceTests
{
    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoopMcp : IMcpClient
    {
        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{}}""");
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, IntegrationSyncService Sync) Create()
    {
        var user = new FakeCurrentUser { TenantId = Guid.NewGuid(), ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DocuEngAIneDbContext(options, user);
        return (db, user, new IntegrationSyncService(db, user, new NoopMcp(), new NoopAudit()));
    }

    private static async Task<IntegrationConnection> AddConnectionAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, IntegrationProvider provider)
    {
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = provider,
            DisplayName = provider.ToString(),
            AuthSecretName = $"kv-{provider}".ToLowerInvariant(),
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    [Fact]
    public async Task Ninja_Adopts_The_Company_Halo_Already_Created_Instead_Of_Duplicating()
    {
        var (db, user, sync) = Create();
        var halo = await AddConnectionAsync(db, user, IntegrationProvider.Halo);
        var haloRun = await sync.SyncFromPayloadAsync(halo.Id, [
            new ExternalCompanyDto("halo-100", "Masri Digital", PrimaryDomain: "masri.tech")
        ]);
        Assert.Equal(1, haloRun.ItemsCreated);

        var ninja = await AddConnectionAsync(db, user, IntegrationProvider.NinjaOne);
        var ninjaRun = await sync.SyncFromPayloadAsync(ninja.Id, [
            new ExternalCompanyDto("2", "Masri Digital")
        ]);

        Assert.Equal(SyncRunStatus.Succeeded, ninjaRun.Status);
        Assert.Equal(0, ninjaRun.ItemsCreated);
        Assert.Equal(1, ninjaRun.ItemsUpdated);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("halo-100", company.HaloClientId);
        Assert.Equal("2", company.NinjaOrganizationId);

        var ids = CompanyIdentity.ReadExternalIds(company.ExternalIdsJson);
        Assert.Equal("halo-100", ids["halo"]);
        Assert.Equal("2", ids["ninja"]);

        // Both connections keep their own mapping row onto the one company.
        var mappings = await db.IntegrationMappings.ToListAsync();
        Assert.Equal(2, mappings.Count);
        Assert.All(mappings, m => Assert.Equal(company.Id, m.LocalEntityId));
    }

    [Fact]
    public async Task Match_Falls_Back_To_Primary_Domain_When_Names_Differ()
    {
        var (db, user, sync) = Create();
        db.Companies.Add(new Company
        {
            TenantId = user.TenantId!.Value,
            Name = "Local Name",
            Slug = "local-name",
            PrimaryDomain = "example.com",
        });
        await db.SaveChangesAsync();

        var halo = await AddConnectionAsync(db, user, IntegrationProvider.Halo);
        var run = await sync.SyncFromPayloadAsync(halo.Id, [
            new ExternalCompanyDto("halo-9", "Totally Different Ltd", PrimaryDomain: "https://WWW.Example.com/portal")
        ]);

        Assert.Equal(0, run.ItemsCreated);
        Assert.Equal(1, run.ItemsUpdated);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("Local Name", company.Name);
        Assert.Equal("halo-9", company.HaloClientId);

        var mapping = await db.IntegrationMappings.SingleAsync();
        Assert.Contains(CompanyMatchIndex.MatchedByDomain, mapping.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ambiguous_Name_Creates_Rather_Than_Merging_The_Wrong_Client()
    {
        var (db, user, sync) = Create();
        db.Companies.Add(new Company { TenantId = user.TenantId!.Value, Name = "Acme", Slug = "acme" });
        db.Companies.Add(new Company { TenantId = user.TenantId!.Value, Name = "A C M E", Slug = "a-c-m-e" });
        await db.SaveChangesAsync();

        var halo = await AddConnectionAsync(db, user, IntegrationProvider.Halo);
        var run = await sync.SyncFromPayloadAsync(halo.Id, [
            new ExternalCompanyDto("halo-7", "ACME")
        ]);

        Assert.Equal(1, run.ItemsCreated);
        Assert.Equal(0, run.ItemsUpdated);
        Assert.Equal(3, await db.Companies.CountAsync());
    }

    [Fact]
    public async Task Cipp_And_Meraki_Ids_Are_Recorded_In_ExternalIdsJson()
    {
        var (db, user, sync) = Create();
        var cipp = await AddConnectionAsync(db, user, IntegrationProvider.Cipp);
        await sync.SyncFromPayloadAsync(cipp.Id, [
            new ExternalCompanyDto("8c65106e-9e7e-45d4-b55a-3cbd4b415a08", "Contoso")
        ]);

        var meraki = await AddConnectionAsync(db, user, IntegrationProvider.Meraki);
        var merakiRun = await sync.SyncFromPayloadAsync(meraki.Id, [
            new ExternalCompanyDto("1279651", "Contoso")
        ]);

        Assert.Equal(1, merakiRun.ItemsUpdated);

        var company = await db.Companies.SingleAsync();
        Assert.Null(company.HaloClientId);
        Assert.Null(company.NinjaOrganizationId);

        var ids = CompanyIdentity.ReadExternalIds(company.ExternalIdsJson);
        Assert.Equal("8c65106e-9e7e-45d4-b55a-3cbd4b415a08", ids["cipp"]);
        Assert.Equal("1279651", ids["meraki"]);
    }

    [Fact]
    public async Task Re_Running_The_Same_Connection_Updates_Through_The_Mapping()
    {
        var (db, user, sync) = Create();
        var halo = await AddConnectionAsync(db, user, IntegrationProvider.Halo);
        ExternalCompanyDto[] payload = [new ExternalCompanyDto("halo-100", "Masri Digital")];

        var first = await sync.SyncFromPayloadAsync(halo.Id, payload);
        var second = await sync.SyncFromPayloadAsync(halo.Id, payload);

        Assert.Equal(1, first.ItemsCreated);
        Assert.Equal(0, second.ItemsCreated);
        Assert.Equal(1, second.ItemsUpdated);
        Assert.Single(await db.Companies.ToListAsync());
        Assert.Single(await db.IntegrationMappings.ToListAsync());
    }

    [Fact]
    public async Task Provider_Id_Match_Beats_A_Different_Company_With_The_Same_Name()
    {
        var (db, user, sync) = Create();
        // The right company already carries the Halo id; a decoy shares the incoming name.
        db.Companies.Add(new Company
        {
            TenantId = user.TenantId!.Value,
            Name = "Renamed In Halo",
            Slug = "renamed-in-halo",
            HaloClientId = "halo-100",
        });
        db.Companies.Add(new Company { TenantId = user.TenantId!.Value, Name = "Masri Digital", Slug = "masri-digital" });
        await db.SaveChangesAsync();

        var halo = await AddConnectionAsync(db, user, IntegrationProvider.Halo);
        await sync.SyncFromPayloadAsync(halo.Id, [new ExternalCompanyDto("halo-100", "Masri Digital")]);

        var mapping = await db.IntegrationMappings.SingleAsync();
        var matched = await db.Companies.SingleAsync(c => c.Id == mapping.LocalEntityId);
        Assert.Equal("Renamed In Halo", matched.Name);
        Assert.Contains(CompanyMatchIndex.MatchedByProviderId, mapping.MetadataJson!, StringComparison.Ordinal);
        Assert.Equal(2, await db.Companies.CountAsync());
    }

    [Fact]
    public void NormalizeName_Ignores_Case_Punctuation_And_Spacing()
    {
        Assert.Equal("examplecoinc", CompanyIdentity.NormalizeName("ExampleCo, Inc."));
        Assert.Equal("examplecoinc", CompanyIdentity.NormalizeName("  example co   inc  "));
        Assert.Null(CompanyIdentity.NormalizeName("   "));
        Assert.Null(CompanyIdentity.NormalizeName("---"));

        // Legal suffixes are deliberately kept: dropping them would merge distinct clients.
        Assert.NotEqual(CompanyIdentity.NormalizeName("Acme LLC"), CompanyIdentity.NormalizeName("Acme"));
    }

    [Fact]
    public void NormalizeDomain_Reduces_Urls_And_Emails_To_A_Bare_Host()
    {
        Assert.Equal("example.com", CompanyIdentity.NormalizeDomain("https://WWW.Example.com/portal?x=1"));
        Assert.Equal("example.com", CompanyIdentity.NormalizeDomain("Example.com"));
        Assert.Equal("example.com", CompanyIdentity.NormalizeDomain("admin@example.com"));
        Assert.Equal("example.com", CompanyIdentity.NormalizeDomain("example.com:8443"));
        Assert.Null(CompanyIdentity.NormalizeDomain(null));
        Assert.Null(CompanyIdentity.NormalizeDomain(" "));
    }

    [Fact]
    public void ExternalIds_Round_Trip_And_Tolerate_Garbage()
    {
        var first = CompanyIdentity.UpsertExternalId(null, "halo", "halo-100");
        var second = CompanyIdentity.UpsertExternalId(first, "ninja", "2");
        var replaced = CompanyIdentity.UpsertExternalId(second, "halo", "halo-999");

        var ids = CompanyIdentity.ReadExternalIds(replaced);
        Assert.Equal("halo-999", ids["halo"]);
        Assert.Equal("2", ids["ninja"]);
        Assert.Equal(2, ids.Count);

        // Unparsable or non-object metadata is treated as absent, never as a sync failure.
        Assert.Empty(CompanyIdentity.ReadExternalIds("not json"));
        Assert.Empty(CompanyIdentity.ReadExternalIds("[1,2,3]"));
        Assert.Equal("halo-1", CompanyIdentity.ReadExternalIds(
            CompanyIdentity.UpsertExternalId("not json", "halo", "halo-1"))["halo"]);

        // Numeric ids survive the round trip as strings.
        Assert.Equal("1279651", CompanyIdentity.ReadExternalIds("""{"meraki":1279651}""")["meraki"]);
    }

    [Fact]
    public void Match_Index_Refuses_Ambiguous_Keys()
    {
        var tenantId = Guid.NewGuid();
        var index = new CompanyMatchIndex([
            new Company { TenantId = tenantId, Name = "Acme", Slug = "acme" },
            new Company { TenantId = tenantId, Name = "A.C.M.E.", Slug = "acme-2" },
            new Company { TenantId = tenantId, Name = "Unique Co", Slug = "unique-co", PrimaryDomain = "unique.example" },
        ]);

        Assert.Null(index.Find("halo", new ExternalCompanyDto("halo-1", "ACME")));

        var hit = index.Find("halo", new ExternalCompanyDto("halo-2", "Unique Co"));
        Assert.NotNull(hit);
        Assert.Equal(CompanyMatchIndex.MatchedByName, hit!.Reason);
    }

    [Fact]
    public void Match_Index_Reads_Provider_Ids_From_ExternalIdsJson()
    {
        var tenantId = Guid.NewGuid();
        var target = new Company
        {
            TenantId = tenantId,
            Name = "Contoso",
            Slug = "contoso",
            ExternalIdsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["cipp"] = "tenant-guid" }),
        };
        var index = new CompanyMatchIndex([target]);

        var hit = index.Find("cipp", new ExternalCompanyDto("tenant-guid", "Something Else Entirely"));
        Assert.NotNull(hit);
        Assert.Equal(target.Id, hit!.Company.Id);
        Assert.Equal(CompanyMatchIndex.MatchedByProviderId, hit.Reason);
    }
}

using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class RunbookTests
{
    private static (DocuEngAIneDbContext Db, Guid TenantId) CreateContext()
    {
        var tenantId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var currentUser = new FakeCurrentUser { TenantId = tenantId, Role = UserRole.Owner };
        var db = new DocuEngAIneDbContext(options, currentUser);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", Slug = "test" });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return (db, tenantId);
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId, string? objectId = null)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = objectId ?? Guid.NewGuid().ToString(),
            Role = UserRole.Owner,
        };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static async Task<(Guid TenantA, Guid TenantB, Guid CompanyA, Guid CompanyB, Guid RunbookA, Guid RunbookB, string DbName)> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantA, Name = "ExampleCo", Slug = "exampleco" };
        var companyB = new Company { TenantId = tenantB, Name = "PoisonCo", Slug = "poisonco" };
        var runbookA = new Runbook { TenantId = tenantA, Title = "Onboard ExampleCo", Slug = "onboard-exampleco", CompanyId = companyA.Id, IsPublished = true };
        var runbookB = new Runbook { TenantId = tenantB, Title = "Poison SOP", Slug = "poison-sop", CompanyId = companyB.Id, IsPublished = true };

        var (dbA, _) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Companies.Add(companyA);
            dbA.Runbooks.Add(runbookA);
            await dbA.SaveChangesAsync();
        }

        var (dbB, _) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Companies.Add(companyB);
            dbB.Runbooks.Add(runbookB);
            await dbB.SaveChangesAsync();
        }

        return (tenantA, tenantB, companyA.Id, companyB.Id, runbookA.Id, runbookB.Id, dbName);
    }

    [Fact]
    public async Task Runbook_Stores_Ordered_Steps()
    {
        var (db, tenantId) = CreateContext();

        var runbook = new Runbook
        {
            TenantId = tenantId,
            Title = "Onboard client",
            Slug = "onboard-client",
            Steps =
            [
                new RunbookStep { Order = 1, Title = "Create tenant" },
                new RunbookStep { Order = 2, Title = "Add primary contact" },
                new RunbookStep { Order = 3, Title = "Configure policies" },
            ],
        };

        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var fetched = await db.Runbooks.Include(r => r.Steps.OrderBy(s => s.Order)).FirstAsync(r => r.Id == runbook.Id);
        Assert.Equal(3, fetched.Steps.Count);
        Assert.Equal("Create tenant", fetched.Steps.First().Title);
    }

    [Fact]
    public async Task ForTenant_Does_Not_Leak_Other_Tenant_Runs()
    {
        var (tenantA, tenantB, _, _, runbookA, runbookB, dbName) = await SeedAsync();

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var started = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), dbA, userA);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(started).StatusCode);
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var started = await RunbookEndpoints.StartRunAsync(runbookB, new StartRunbookRunRequest(), dbB, userB);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(started).StatusCode);
        }

        var (queryA, queryUserA) = Open(dbName, tenantA);
        await using (queryA)
        {
            var runs = await queryA.RunbookRuns.ForTenant(queryUserA).ToListAsync();
            Assert.Single(runs);
            Assert.Equal(runbookA, runs[0].RunbookId);
            Assert.DoesNotContain(runs, r => r.RunbookId == runbookB);

            var listed = Assert.IsAssignableFrom<IValueHttpResult>(
                await RunbookEndpoints.ListRunsAsync(runbookA, queryA, queryUserA));
            var items = Assert.IsAssignableFrom<IReadOnlyList<RunbookRunItem>>(listed.Value);
            Assert.Single(items);
            Assert.All(items, i => Assert.Equal(runbookA, i.RunbookId));

            var hiddenBook = await RunbookEndpoints.ListRunsAsync(runbookB, queryA, queryUserA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(hiddenBook).StatusCode);
        }
    }

    [Fact]
    public async Task Cannot_Start_Run_On_Other_Tenant_Runbook()
    {
        var (tenantA, _, _, companyB, _, runbookB, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var started = await RunbookEndpoints.StartRunAsync(runbookB, new StartRunbookRunRequest(), db, user);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(started).StatusCode);

            var withPoisonCompany = await RunbookEndpoints.StartRunAsync(
                runbookB,
                new StartRunbookRunRequest(companyB),
                db,
                user);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(withPoisonCompany).StatusCode);

            Assert.Empty(await db.RunbookRuns.ForTenant(user).ToListAsync());
            Assert.Empty(await db.RunbookRuns.ToListAsync());
        }
    }

    [Fact]
    public async Task Cannot_Start_Run_With_Other_Tenant_Company()
    {
        var (tenantA, _, _, companyB, runbookA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var started = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(companyB), db, user);
            var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(started);
            Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
            var value = Assert.IsAssignableFrom<IValueHttpResult>(started);
            Assert.Equal("Company not found.", value.Value);
            Assert.Empty(await db.RunbookRuns.ForTenant(user).ToListAsync());
        }
    }

    [Fact]
    public async Task RunCount_Increments_On_List_And_Detail()
    {
        var (tenantA, _, _, _, runbookA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA, objectId: "entra-user-a");
        await using (db)
        {
            var before = await RunbookEndpoints.ListPublishedAsync(db, user);
            Assert.Single(before);
            Assert.Equal(0, before[0].RunCount);

            var detailBefore = Assert.IsType<RunbookDetailItem>(
                Assert.IsAssignableFrom<IValueHttpResult>(await RunbookEndpoints.GetAsync(runbookA, db, user)).Value);
            Assert.Equal(0, detailBefore.RunCount);

            var first = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(first).StatusCode);
            var created = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(first).Value);
            Assert.Equal(RunbookRunStatus.Running, created.Status);
            Assert.Equal("entra-user-a", created.StartedByObjectId);

            var mid = await RunbookEndpoints.ListPublishedAsync(db, user);
            Assert.Equal(1, mid[0].RunCount);

            var second = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(second).StatusCode);

            var after = await RunbookEndpoints.ListPublishedAsync(db, user);
            Assert.Equal(2, after[0].RunCount);

            var detailAfter = Assert.IsType<RunbookDetailItem>(
                Assert.IsAssignableFrom<IValueHttpResult>(await RunbookEndpoints.GetAsync(runbookA, db, user)).Value);
            Assert.Equal(2, detailAfter.RunCount);
        }
    }

    [Fact]
    public async Task Complete_And_Cancel_Only_While_Running()
    {
        var (tenantA, tenantB, _, _, runbookA, _, dbName) = await SeedAsync();
        Guid runId;
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var started = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);
            runId = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(started).Value).Id;

            var completed = await RunbookEndpoints.CompleteRunAsync(runbookA, runId, db, user);
            var done = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(completed).Value);
            Assert.Equal(RunbookRunStatus.Completed, done.Status);
            Assert.NotNull(done.FinishedAt);

            var again = await RunbookEndpoints.CompleteRunAsync(runbookA, runId, db, user);
            Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(again).StatusCode);
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var stolen = await RunbookEndpoints.CompleteRunAsync(runbookA, runId, dbB, userB);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(stolen).StatusCode);
            var cancelled = await RunbookEndpoints.CancelRunAsync(runbookA, runId, dbB, userB);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(cancelled).StatusCode);
        }
    }
}

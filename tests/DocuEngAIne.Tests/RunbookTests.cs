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

    [Fact]
    public async Task Recent_Rollup_Does_Not_Leak_Other_Tenant_Runs()
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
            await RunbookEndpoints.CompleteRunAsync(
                runbookB,
                Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(started).Value).Id,
                dbB,
                userB);
        }

        var (queryA, queryUserA) = Open(dbName, tenantA);
        await using (queryA)
        {
            var items = await RunbookEndpoints.ListRecentRunsAsync(queryA, queryUserA);
            Assert.Single(items);
            Assert.Equal(runbookA, items[0].RunbookId);
            Assert.Equal("Onboard ExampleCo", items[0].RunbookTitle);
            Assert.Equal("ExampleCo", items[0].CompanyName);
            Assert.Equal(RunbookRunStatus.Running, items[0].Status);
            Assert.DoesNotContain(items, i => i.RunbookId == runbookB);
            Assert.DoesNotContain(items, i => i.RunbookTitle.Contains("Poison", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(items, i => i.CompanyName == "PoisonCo");

            var hidden = await RunbookEndpoints.ListRecentRunsAsync(queryA, queryUserA, status: RunbookRunStatus.Completed);
            Assert.Empty(hidden);
        }

        var (queryB, queryUserB) = Open(dbName, tenantB);
        await using (queryB)
        {
            var items = await RunbookEndpoints.ListRecentRunsAsync(queryB, queryUserB);
            Assert.Single(items);
            Assert.Equal(runbookB, items[0].RunbookId);
            Assert.Equal("Poison SOP", items[0].RunbookTitle);
            Assert.Equal("PoisonCo", items[0].CompanyName);
            Assert.Equal(RunbookRunStatus.Completed, items[0].Status);
            Assert.DoesNotContain(items, i => i.RunbookId == runbookA);
            Assert.DoesNotContain(items, i => i.CompanyName == "ExampleCo");
        }
    }

    [Fact]
    public async Task Recent_Rollup_CompanyId_Uses_ForTenant_And_Does_Not_Five_Hundred()
    {
        var (tenantA, _, companyA, companyB, runbookA, _, dbName) = await SeedAsync();
        var companyAOnly = new Company { TenantId = tenantA, Name = "OtherCo", Slug = "otherco" };

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            db.Companies.Add(companyAOnly);
            await db.SaveChangesAsync();

            var first = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(companyA), db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(first).StatusCode);

            var second = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(companyAOnly.Id), db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(second).StatusCode);

            var otherTenant = await RunbookEndpoints.ListRecentRunsAsync(db, user, companyId: companyB);
            Assert.Empty(otherTenant);

            var missing = await RunbookEndpoints.ListRecentRunsAsync(db, user, companyId: Guid.NewGuid());
            Assert.Empty(missing);

            var scoped = await RunbookEndpoints.ListRecentRunsAsync(db, user, companyId: companyA);
            Assert.Single(scoped);
            Assert.All(scoped, i => Assert.Equal(companyA, i.CompanyId));
            Assert.Equal("ExampleCo", scoped[0].CompanyName);
            Assert.DoesNotContain(scoped, i => i.CompanyName == "OtherCo");
            Assert.DoesNotContain(scoped, i => i.CompanyName == "PoisonCo");

            var otherCo = await RunbookEndpoints.ListRecentRunsAsync(db, user, companyId: companyAOnly.Id);
            Assert.Single(otherCo);
            Assert.Equal("OtherCo", otherCo[0].CompanyName);
        }
    }

    [Fact]
    public async Task Recent_Rollup_Filters_Status_And_Orders_Recent_First()
    {
        var (tenantA, _, _, _, runbookA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var first = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);
            var firstId = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(first).Value).Id;
            await RunbookEndpoints.CompleteRunAsync(runbookA, firstId, db, user);

            var second = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);
            var secondId = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(second).Value).Id;
            await RunbookEndpoints.CancelRunAsync(runbookA, secondId, db, user);

            var third = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);
            var thirdId = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(third).Value).Id;

            var all = await RunbookEndpoints.ListRecentRunsAsync(db, user);
            Assert.Equal(3, all.Count);
            Assert.Equal(new[] { thirdId, secondId, firstId }, all.Select(i => i.Id).ToArray());
            Assert.Equal("Onboard ExampleCo", all[0].RunbookTitle);
            Assert.NotEqual(default, all[0].StartedAt);
            Assert.Null(all[0].FinishedAt);
            Assert.NotNull(all[1].FinishedAt);

            var running = await RunbookEndpoints.ListRecentRunsAsync(db, user, status: RunbookRunStatus.Running);
            Assert.Single(running);
            Assert.Equal(thirdId, running[0].Id);

            var completed = await RunbookEndpoints.ListRecentRunsAsync(db, user, status: RunbookRunStatus.Completed);
            Assert.Single(completed);
            Assert.Equal(firstId, completed[0].Id);
            Assert.NotNull(completed[0].FinishedAt);

            var cancelled = await RunbookEndpoints.ListRecentRunsAsync(db, user, status: RunbookRunStatus.Cancelled);
            Assert.Single(cancelled);
            Assert.Equal(secondId, cancelled[0].Id);
        }
    }

    [Fact]
    public async Task Recent_Rollup_Unknown_Status_Is_Bad_Request()
    {
        var (tenantA, _, _, _, runbookA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);

            var bad = await RunbookEndpoints.ListRecentRunsResultAsync("poison", null, db, user);
            Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(bad).StatusCode);
            Assert.Equal(RunbookEndpoints.UnknownStatusMessage, Assert.IsAssignableFrom<IValueHttpResult>(bad).Value);

            var ok = Assert.IsAssignableFrom<IValueHttpResult>(
                await RunbookEndpoints.ListRecentRunsResultAsync("completed", null, db, user));
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<RunbookRunRollupItem>>(ok.Value));

            var running = Assert.IsAssignableFrom<IValueHttpResult>(
                await RunbookEndpoints.ListRecentRunsResultAsync("Running", null, db, user));
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<RunbookRunRollupItem>>(running.Value));
        }
    }

    [Fact]
    public async Task Promote_Creates_Document_From_Completed_Run()
    {
        var (tenantA, _, companyA, _, runbookA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            db.RunbookSteps.Add(new RunbookStep { RunbookId = runbookA, Order = 1, Title = "Create tenant", Details = "Open the portal." });
            db.RunbookSteps.Add(new RunbookStep { RunbookId = runbookA, Order = 2, Title = "Add primary contact" });
            await db.SaveChangesAsync();

            var started = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);
            var runId = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(started).Value).Id;
            var completed = await RunbookEndpoints.CompleteRunAsync(runbookA, runId, db, user);
            var done = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(completed).Value);

            var promoted = await RunbookEndpoints.PromoteRunAsync(runbookA, runId, db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(promoted).StatusCode);
            var created = Assert.IsType<PromoteRunResult>(Assert.IsAssignableFrom<IValueHttpResult>(promoted).Value);

            db.ChangeTracker.Clear();
            var doc = await db.Documents.ForTenant(user).AsNoTracking().SingleAsync();
            Assert.Equal(created.Id, doc.Id);
            Assert.Equal(companyA, doc.CompanyId);
            Assert.Equal($"Onboard ExampleCo — {done.FinishedAt!.Value.UtcDateTime:yyyy-MM-dd}", doc.Title);
            Assert.Equal($"run-{runId:N}", doc.Slug);
            Assert.Contains("Status: Completed", doc.Content, StringComparison.Ordinal);
            Assert.Contains("1. Create tenant", doc.Content, StringComparison.Ordinal);
            Assert.Contains("Open the portal.", doc.Content, StringComparison.Ordinal);
            Assert.Contains("2. Add primary contact", doc.Content, StringComparison.Ordinal);
            Assert.Empty(await db.DocumentVersions.Where(v => v.DocumentId == doc.Id).ToListAsync());
        }
    }

    [Fact]
    public async Task Promote_Second_Call_Does_Not_Duplicate()
    {
        var (tenantA, _, _, _, runbookA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var started = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);
            var runId = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(started).Value).Id;
            await RunbookEndpoints.CompleteRunAsync(runbookA, runId, db, user);

            var first = await RunbookEndpoints.PromoteRunAsync(runbookA, runId, db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(first).StatusCode);
            var created = Assert.IsType<PromoteRunResult>(Assert.IsAssignableFrom<IValueHttpResult>(first).Value);

            var second = await RunbookEndpoints.PromoteRunAsync(runbookA, runId, db, user);
            Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(second).StatusCode);
            var again = Assert.IsType<PromoteRunResult>(Assert.IsAssignableFrom<IValueHttpResult>(second).Value);
            Assert.Equal(created.Id, again.Id);

            Assert.Equal(1, await db.Documents.ForTenant(user).CountAsync());
            Assert.Empty(await db.DocumentVersions.ToListAsync());
        }
    }

    [Fact]
    public async Task Promote_Other_Tenant_Is_Not_Found()
    {
        var (tenantA, tenantB, _, _, runbookA, _, dbName) = await SeedAsync();
        Guid runId;
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var started = await RunbookEndpoints.StartRunAsync(runbookA, new StartRunbookRunRequest(), db, user);
            runId = Assert.IsType<RunbookRunItem>(Assert.IsAssignableFrom<IValueHttpResult>(started).Value).Id;
            await RunbookEndpoints.CompleteRunAsync(runbookA, runId, db, user);
            var promoted = await RunbookEndpoints.PromoteRunAsync(runbookA, runId, db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(promoted).StatusCode);
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var stolen = await RunbookEndpoints.PromoteRunAsync(runbookA, runId, dbB, userB);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(stolen).StatusCode);
            Assert.Empty(await dbB.Documents.ForTenant(userB).ToListAsync());
            Assert.Equal(1, await dbB.Documents.CountAsync());
        }
    }
}

using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Audit;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DocuEngAIne.Tests;

public class AuditEventsTests
{
    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = Guid.NewGuid().ToString(),
            Email = "tech@example.test",
            Role = UserRole.Owner,
        };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static T ValueOf<T>(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return Assert.IsType<T>(value.Value);
    }

    [Fact]
    public async Task Rich_Log_Persists_Category_Label_Changes_And_Actor_Name()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId);
        await using (db)
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "A", Slug = "a" });
            db.Users.Add(new User
            {
                TenantId = tenantId,
                EntraObjectId = user.ObjectId!,
                Email = "tech@example.test",
                DisplayName = "Tech One",
                Role = UserRole.Owner,
            });
            await db.SaveChangesAsync();

            var audit = new AuditService(db, user, new HttpContextAccessor());
            await audit.LogAsync(new AuditEntry(
                "Document.Update",
                nameof(Document),
                Guid.NewGuid(),
                "Renamed",
                Category: AuditCategories.Resource,
                TargetLabel: "Onboarding SOP",
                ChangesJson: """{"title":{"from":"Old","to":"New"}}"""));

            var row = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "Document.Update");
            Assert.Equal(AuditCategories.Resource, row.Category);
            Assert.Equal("Onboarding SOP", row.TargetLabel);
            Assert.Contains("\"from\":\"Old\"", row.ChangesJson);
            Assert.Equal("Tech One", row.ActorName);
            Assert.Equal(user.ObjectId, row.ActorObjectId);
            Assert.Equal(tenantId, row.TenantId);
        }
    }

    [Fact]
    public async Task Simple_Log_Overload_Still_Works_Through_The_Rich_Path()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId);
        await using (db)
        {
            var audit = new AuditService(db, user, new HttpContextAccessor());
            await audit.LogAsync("Asset.Create", nameof(Asset), Guid.NewGuid(), "made one");

            var row = await db.AuditLogs.AsNoTracking().SingleAsync();
            Assert.Equal("Asset.Create", row.Action);
            Assert.Null(row.Category);
            Assert.Null(row.ChangesJson);
        }
    }

    private static async Task<(string DbName, Guid TenantA, Guid TenantB, Guid DocId)> SeedEventsAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var docId = Guid.NewGuid();

        var (db, _) = Open(dbName, tenantA);
        await using (db)
        {
            db.AuditLogs.AddRange(
                new AuditLog
                {
                    TenantId = tenantA,
                    Action = "Document.Update",
                    Category = "resource",
                    EntityType = nameof(Document),
                    EntityId = docId,
                    TargetLabel = "A-Doc",
                },
                new AuditLog
                {
                    TenantId = tenantA,
                    Action = "KeeperLink.Reveal",
                    Category = "access",
                    EntityType = nameof(KeeperLink),
                    EntityId = Guid.NewGuid(),
                },
                new AuditLog
                {
                    TenantId = tenantA,
                    Action = "Document.Create",
                    Category = "resource",
                    EntityType = nameof(Document),
                    EntityId = docId,
                },
                new AuditLog
                {
                    TenantId = tenantB,
                    Action = "Document.Update",
                    Category = "resource",
                    EntityType = nameof(Document),
                    EntityId = Guid.NewGuid(),
                    TargetLabel = "Poison-Doc",
                });
            await db.SaveChangesAsync();
        }

        return (dbName, tenantA, tenantB, docId);
    }

    [Fact]
    public async Task List_Filters_Pages_And_Never_Leaks_Other_Tenants()
    {
        var seed = await SeedEventsAsync();
        var (db, user) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var all = ValueOf<AuditEndpoints.AuditEventPage>(await AuditEndpoints.ListAsync(
                null, null, null, null, null, null, null, null, null, db, user));
            Assert.Equal(3, all.Total);
            Assert.DoesNotContain(all.Items, i => i.TargetLabel == "Poison-Doc");

            var access = ValueOf<AuditEndpoints.AuditEventPage>(await AuditEndpoints.ListAsync(
                null, "access", null, null, null, null, null, null, null, db, user));
            Assert.Single(access.Items);
            Assert.Equal("KeeperLink.Reveal", access.Items[0].Action);

            var paged = ValueOf<AuditEndpoints.AuditEventPage>(await AuditEndpoints.ListAsync(
                null, null, null, null, null, null, null, 2, 2, db, user));
            Assert.Equal(3, paged.Total);
            Assert.Single(paged.Items);
            Assert.Equal(2, paged.Page);
        }
    }

    [Fact]
    public async Task Get_Is_Tenant_Scoped()
    {
        var seed = await SeedEventsAsync();
        Guid foreignId;
        var (dbB, _) = Open(seed.DbName, seed.TenantB);
        await using (dbB)
        {
            foreignId = (await dbB.AuditLogs.AsNoTracking().SingleAsync(a => a.TenantId == seed.TenantB)).Id;
        }

        var (db, user) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var own = await db.AuditLogs.AsNoTracking().FirstAsync(a => a.TenantId == seed.TenantA);
            var found = await AuditEndpoints.GetAsync(own.Id, db, user);
            Assert.Equal(own.Id, ValueOf<AuditEndpoints.AuditEventItem>(found).Id);

            var missing = await AuditEndpoints.GetAsync(foreignId, db, user);
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(missing);
            Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)missing).StatusCode);
        }
    }

    [Fact]
    public async Task Activity_Returns_Only_The_Target_Rows_Newest_First()
    {
        var seed = await SeedEventsAsync();
        var (db, user) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var result = await AuditEndpoints.ActivityAsync(nameof(Document), seed.DocId, db, user);
            var items = ValueOf<List<AuditEndpoints.AuditEventItem>>(result);
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal(seed.DocId, i.EntityId));
            Assert.True(items[0].OccurredAt >= items[1].OccurredAt);
        }
    }

    [Fact]
    public async Task Export_Produces_Escaped_Csv_And_Audits_The_Export()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId);
        await using (db)
        {
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId,
                Action = "Document.Update",
                EntityType = nameof(Document),
                // Hostile label: opening this in Excel unescaped would execute as a formula.
                TargetLabel = "=cmd|' /C calc'!A0",
                Details = "has \"quotes\", and, commas",
            });
            await db.SaveChangesAsync();

            var audit = new AuditService(db, user, new HttpContextAccessor());
            var result = await AuditEndpoints.ExportAsync(
                null, null, null, null, null, null, null, db, user, audit);

            var file = Assert.IsAssignableFrom<IFileHttpResult>(result);
            Assert.Equal("text/csv", file.ContentType);

            var exported = await db.AuditLogs.AsNoTracking()
                .SingleAsync(a => a.Action == "Audit.Export");
            Assert.Equal(AuditCategories.Export, exported.Category);
        }
    }

    [Fact]
    public void CsvField_Defuses_Formula_Injection_And_Escapes_Quotes()
    {
        Assert.Equal("\"'=SUM(A1)\"", AuditEndpoints.CsvField("=SUM(A1)"));
        Assert.Equal("\"'+1\"", AuditEndpoints.CsvField("+1"));
        Assert.Equal("\"'-1\"", AuditEndpoints.CsvField("-1"));
        Assert.Equal("\"'@cmd\"", AuditEndpoints.CsvField("@cmd"));
        Assert.Equal("\"say \"\"hi\"\"\"", AuditEndpoints.CsvField("say \"hi\""));
        Assert.Equal("", AuditEndpoints.CsvField(null));

        var csv = AuditEndpoints.BuildCsv([
            new AuditLog { Action = "A", EntityType = "Document", TargetLabel = "=evil" },
        ]);
        Assert.Contains("\"'=evil\"", csv);
        Assert.StartsWith("occurred_at,action,category", csv);
    }

    [Fact]
    public async Task Retention_Purges_Only_Rows_Past_The_Window_And_Respects_Disabled()
    {
        var tenantId = Guid.NewGuid();
        var (db, _) = Open(Guid.NewGuid().ToString(), tenantId);
        await using (db)
        {
            var old = new AuditLog { TenantId = tenantId, Action = "Old", EntityType = "X" };
            var fresh = new AuditLog { TenantId = tenantId, Action = "Fresh", EntityType = "X" };
            db.AuditLogs.AddRange(old, fresh);
            await db.SaveChangesAsync();

            // SaveChanges stamps CreatedAt = now on insert; age the old row past the window by hand.
            old.CreatedAt = DateTimeOffset.UtcNow.AddDays(-400);
            await db.SaveChangesAsync();

            var enabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuditRetentionService.RetentionDaysKey] = "365",
                [AuditRetentionService.ChunkSizeKey] = "1",
            }).Build();
            var removed = await new AuditRetentionService(db, enabled).PurgeAsync();
            Assert.Equal(1, removed);
            Assert.Equal("Fresh", (await db.AuditLogs.AsNoTracking().SingleAsync()).Action);

            var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuditRetentionService.RetentionDaysKey] = "0",
            }).Build();
            Assert.Equal(0, await new AuditRetentionService(db, disabled).PurgeAsync());
            Assert.Equal(1, await db.AuditLogs.CountAsync());
        }
    }
}

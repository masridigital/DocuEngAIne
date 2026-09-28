using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class AccessReviewTests
{
    private sealed class RecordingAudit : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(action, entityType, entityId, details));
            return Task.CompletedTask;
        }

        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(
        string dbName, Guid tenantId, string objectId, UserRole claimRole = UserRole.Owner)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = objectId,
            Email = $"{objectId}@example.com",
            DisplayName = objectId,
            Role = claimRole,
        };
        var db = new DocuEngAIneDbContext(
            new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options, user);
        return (db, user);
    }

    private static User NewUser(Guid tenantId, string objectId, UserRole role, bool isActive = true) => new()
    {
        TenantId = tenantId,
        EntraObjectId = objectId,
        Email = $"{objectId}@example.com",
        DisplayName = objectId,
        Role = role,
        IsActive = isActive,
    };

    private static T ValueOf<T>(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return Assert.IsType<T>(value.Value);
    }

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    /// <summary>Tenant with an Owner (the caller), an Admin, a Contributor with one grant, and a suspended Reader.</summary>
    private static async Task<(DocuEngAIneDbContext Db, FakeCurrentUser Owner, Guid ReviewId)> StartedReviewAsync(string dbName, Guid tenantId)
    {
        var (db, owner) = Open(dbName, tenantId, "owner");
        db.Users.AddRange(
            NewUser(tenantId, "owner", UserRole.Owner),
            NewUser(tenantId, "admin", UserRole.Admin),
            NewUser(tenantId, "tech", UserRole.Contributor),
            NewUser(tenantId, "gone", UserRole.Reader, isActive: false));
        await db.SaveChangesAsync();

        var tech = await db.Users.SingleAsync(u => u.EntraObjectId == "tech");
        db.ResourceRoleAssignments.Add(new ResourceRoleAssignment
        {
            TenantId = tenantId,
            UserId = tech.Id,
            ResourceType = ResourceType.Document,
            ResourceId = Guid.NewGuid(),
            Role = UserRole.Admin,
        });
        await db.SaveChangesAsync();

        var created = await AccessReviewEndpoints.CreateAsync(
            new AccessReviewEndpoints.CreateAccessReviewRequest("Q3 access review"), db, owner);
        var reviewId = ValueOf<AccessReviewEndpoints.AccessReviewSummary>(created).Id;
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await AccessReviewEndpoints.StartAsync(reviewId, db, owner)));
        return (db, owner, reviewId);
    }

    private static async Task<AccessReviewItem> ItemForAsync(DocuEngAIneDbContext db, Guid reviewId, string objectId)
    {
        var userId = (await db.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == objectId)).Id;
        return await db.AccessReviewItems.AsNoTracking().SingleAsync(i => i.AccessReviewId == reviewId && i.SubjectUserId == userId);
    }

    [Fact]
    public async Task Start_Snapshots_Every_Active_User_With_Role_And_Grants()
    {
        var (db, _, reviewId) = await StartedReviewAsync(Guid.NewGuid().ToString(), Guid.NewGuid());
        await using (db)
        {
            var items = await db.AccessReviewItems.AsNoTracking().Where(i => i.AccessReviewId == reviewId).ToListAsync();
            // The suspended Reader is not part of the population.
            Assert.Equal(3, items.Count);
            Assert.DoesNotContain(items, i => i.SubjectEmail.StartsWith("gone"));
            Assert.All(items, i => Assert.Equal(AccessReviewDecision.Pending, i.Decision));

            var tech = Assert.Single(items, i => i.SubjectEmail.StartsWith("tech"));
            Assert.Equal(UserRole.Contributor, tech.RoleAtSnapshot);
            Assert.Equal(1, tech.GrantCount);
            Assert.Contains("\"grants\"", tech.AccessSnapshotJson);

            Assert.Equal(AccessReviewStatus.InProgress, (await db.AccessReviews.AsNoTracking().SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task Revoke_Deactivates_The_User_Immediately()
    {
        var (db, owner, reviewId) = await StartedReviewAsync(Guid.NewGuid().ToString(), Guid.NewGuid());
        await using (db)
        {
            var item = await ItemForAsync(db, reviewId, "tech");
            var audit = new RecordingAudit();
            var result = await AccessReviewEndpoints.DecideAsync(
                reviewId, item.Id, new AccessReviewEndpoints.DecideItemRequest(AccessReviewDecision.Revoke, Notes: "left the company"), db, owner, audit);

            Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
            Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == "tech")).IsActive);
            var decided = await ItemForAsync(db, reviewId, "tech");
            Assert.Equal(AccessReviewDecision.Revoke, decided.Decision);
            Assert.Equal("left the company", decided.DecisionNotes);
            Assert.Contains(audit.Entries, e => e.Action == "AccessReview.Decide");
            Assert.Contains(audit.Entries, e => e.Action == "User.Deactivate");

            // One decision per item.
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await AccessReviewEndpoints.DecideAsync(
                reviewId, item.Id, new AccessReviewEndpoints.DecideItemRequest(AccessReviewDecision.Retain), db, owner)));
        }
    }

    [Fact]
    public async Task Change_Role_Applies_Immediately_And_Requires_A_Different_Role()
    {
        var (db, owner, reviewId) = await StartedReviewAsync(Guid.NewGuid().ToString(), Guid.NewGuid());
        await using (db)
        {
            var item = await ItemForAsync(db, reviewId, "admin");

            var same = await AccessReviewEndpoints.DecideAsync(
                reviewId, item.Id, new AccessReviewEndpoints.DecideItemRequest(AccessReviewDecision.ChangeRole, UserRole.Admin), db, owner);
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(same));

            var changed = await AccessReviewEndpoints.DecideAsync(
                reviewId, item.Id, new AccessReviewEndpoints.DecideItemRequest(AccessReviewDecision.ChangeRole, UserRole.Reader), db, owner);
            Assert.Equal(StatusCodes.Status200OK, StatusOf(changed));
            Assert.Equal(UserRole.Reader, (await db.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == "admin")).Role);
            // The snapshot is evidence of access at start and is not rewritten.
            Assert.Equal(UserRole.Admin, (await ItemForAsync(db, reviewId, "admin")).RoleAtSnapshot);
        }
    }

    [Fact]
    public async Task Reviewer_Cannot_Revoke_Or_Change_Their_Own_Access_But_May_Retain()
    {
        var (db, owner, reviewId) = await StartedReviewAsync(Guid.NewGuid().ToString(), Guid.NewGuid());
        await using (db)
        {
            var self = await ItemForAsync(db, reviewId, "owner");

            var revoke = await AccessReviewEndpoints.DecideAsync(
                reviewId, self.Id, new AccessReviewEndpoints.DecideItemRequest(AccessReviewDecision.Revoke), db, owner);
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(revoke));
            Assert.Equal(AccessReviewEndpoints.SelfDecisionMessage, Assert.IsAssignableFrom<IValueHttpResult>(revoke).Value);

            var change = await AccessReviewEndpoints.DecideAsync(
                reviewId, self.Id, new AccessReviewEndpoints.DecideItemRequest(AccessReviewDecision.ChangeRole, UserRole.Reader), db, owner);
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(change));
            Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == "owner")).IsActive);

            var retain = await AccessReviewEndpoints.DecideAsync(
                reviewId, self.Id, new AccessReviewEndpoints.DecideItemRequest(AccessReviewDecision.Retain), db, owner);
            Assert.Equal(StatusCodes.Status200OK, StatusOf(retain));
        }
    }

    [Fact]
    public async Task Admin_Reviewer_Cannot_Revoke_The_Last_Owner()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (seed, _, reviewId) = await StartedReviewAsync(dbName, tenantId);
        await seed.DisposeAsync();

        var (db, admin) = Open(dbName, tenantId, "admin", UserRole.Admin);
        await using (db)
        {
            var ownerItem = await ItemForAsync(db, reviewId, "owner");
            var result = await AccessReviewEndpoints.DecideAsync(
                reviewId, ownerItem.Id, new AccessReviewEndpoints.DecideItemRequest(AccessReviewDecision.Revoke), db, admin);

            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
            Assert.Equal(UserEndpoints.LastOwnerDeactivateMessage, Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
            Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == "owner")).IsActive);
            Assert.Equal(AccessReviewDecision.Pending, (await ItemForAsync(db, reviewId, "owner")).Decision);
        }
    }

    [Fact]
    public async Task Complete_Is_Refused_While_Items_Are_Pending()
    {
        var (db, owner, reviewId) = await StartedReviewAsync(Guid.NewGuid().ToString(), Guid.NewGuid());
        await using (db)
        {
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await AccessReviewEndpoints.CompleteAsync(reviewId, db, owner)));

            foreach (var objectId in new[] { "owner", "admin", "tech" })
            {
                var item = await ItemForAsync(db, reviewId, objectId);
                Assert.Equal(StatusCodes.Status200OK, StatusOf(await AccessReviewEndpoints.DecideAsync(
                    reviewId, item.Id, new AccessReviewEndpoints.DecideItemRequest(AccessReviewDecision.Retain), db, owner)));
            }

            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await AccessReviewEndpoints.CompleteAsync(reviewId, db, owner)));
            var review = await db.AccessReviews.AsNoTracking().SingleAsync();
            Assert.Equal(AccessReviewStatus.Completed, review.Status);
            Assert.NotNull(review.CompletedAt);

            // A completed review can be neither cancelled nor decided further.
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await AccessReviewEndpoints.CancelAsync(reviewId, db, owner)));
        }
    }

    [Fact]
    public async Task Draft_Can_Only_Start_Once_And_Can_Be_Cancelled()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, owner) = Open(dbName, tenantId, "owner");
        await using (db)
        {
            db.Users.Add(NewUser(tenantId, "owner", UserRole.Owner));
            await db.SaveChangesAsync();

            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await AccessReviewEndpoints.CreateAsync(
                new AccessReviewEndpoints.CreateAccessReviewRequest("  "), db, owner)));
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await AccessReviewEndpoints.CreateAsync(
                new AccessReviewEndpoints.CreateAccessReviewRequest("Bad reviewer", Guid.NewGuid()), db, owner)));

            var id = ValueOf<AccessReviewEndpoints.AccessReviewSummary>(await AccessReviewEndpoints.CreateAsync(
                new AccessReviewEndpoints.CreateAccessReviewRequest("Annual"), db, owner)).Id;
            Assert.Equal(StatusCodes.Status200OK, StatusOf(await AccessReviewEndpoints.StartAsync(id, db, owner)));
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await AccessReviewEndpoints.StartAsync(id, db, owner)));

            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await AccessReviewEndpoints.CancelAsync(id, db, owner)));
            Assert.Equal(AccessReviewStatus.Cancelled, (await db.AccessReviews.AsNoTracking().SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task Reviews_Are_Tenant_Scoped()
    {
        var dbName = Guid.NewGuid().ToString();
        var (seed, _, reviewId) = await StartedReviewAsync(dbName, Guid.NewGuid());
        await seed.DisposeAsync();

        var (db, stranger) = Open(dbName, Guid.NewGuid(), "stranger");
        await using (db)
        {
            Assert.Empty(ValueOf<List<AccessReviewEndpoints.AccessReviewSummary>>(await AccessReviewEndpoints.ListAsync(db, stranger)));
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await AccessReviewEndpoints.GetAsync(reviewId, db, stranger)));
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await AccessReviewEndpoints.CompleteAsync(reviewId, db, stranger)));
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await AccessReviewEndpoints.ExportAsync(reviewId, db, stranger)));
        }
    }

    [Fact]
    public async Task Export_Is_Csv_Evidence_Of_Every_Item()
    {
        var (db, owner, reviewId) = await StartedReviewAsync(Guid.NewGuid().ToString(), Guid.NewGuid());
        await using (db)
        {
            var audit = new RecordingAudit();
            var result = await AccessReviewEndpoints.ExportAsync(reviewId, db, owner, audit);

            var file = Assert.IsAssignableFrom<IFileHttpResult>(result);
            Assert.Equal("text/csv", file.ContentType);
            Assert.Contains(audit.Entries, e => e.Action == "AccessReview.Export");
        }
    }
}

using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Asset external ids and Halo/Ninja deep links mirror Company: typed URL columns plus
/// <c>ExternalIdsJson</c>. URLs only — no secrets. Sync is not wired here.
/// </summary>
public class AssetDeepLinkTests
{
    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, ResourceAuthorizationService Auth) Open(
        string dbName,
        Guid tenantId)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = Guid.NewGuid().ToString(),
            Role = UserRole.Owner,
        };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var db = new DocuEngAIneDbContext(options, user);
        return (db, user, new ResourceAuthorizationService(db, user));
    }

    private static async Task<Guid> SeedTypeAsync(string dbName, Guid tenantId, string name = "Computer")
    {
        var (db, _, _) = Open(dbName, tenantId);
        await using (db)
        {
            var type = new AssetType { TenantId = tenantId, Name = name };
            db.AssetTypes.Add(type);
            await db.SaveChangesAsync();
            return type.Id;
        }
    }

    private static void AssertStatus(int expected, IResult result) =>
        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

    [Fact]
    public async Task Deep_Links_And_External_Ids_Round_Trip_On_Create_And_Update()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var typeId = await SeedTypeAsync(dbName, tenantId);
        var haloUrl = "https://halo.example/asset/42";
        var ninjaUrl = "https://ninja.example/devices/99";
        var idsJson = CompanyIdentity.UpsertExternalId(
            CompanyIdentity.UpsertExternalId(null, CompanyIdentity.HaloKey, "halo-asset-42"),
            CompanyIdentity.NinjaKey,
            "ninja-device-99");

        Guid assetId;
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var created = await AssetEndpoints.PostAsync(
                new CreateAssetRequest(
                    "Firewall",
                    typeId,
                    null,
                    null,
                    null,
                    HaloAssetUrl: $"  {haloUrl}  ",
                    NinjaDeviceUrl: ninjaUrl,
                    ExternalIdsJson: $"  {idsJson}  "),
                db,
                user,
                auth);
            AssertStatus(StatusCodes.Status201Created, created);

            var stored = await db.Assets.ForTenant(user).SingleAsync();
            assetId = stored.Id;
            Assert.Equal(haloUrl, stored.HaloAssetUrl);
            Assert.Equal(ninjaUrl, stored.NinjaDeviceUrl);
            Assert.Equal(idsJson, stored.ExternalIdsJson);
            Assert.Equal("halo-asset-42", CompanyIdentity.ReadExternalIds(stored.ExternalIdsJson)["halo"]);
            Assert.Equal("ninja-device-99", CompanyIdentity.ReadExternalIds(stored.ExternalIdsJson)["ninja"]);
            Assert.Null(stored.GetType().GetProperty("AuthSecretName"));
            Assert.Null(stored.GetType().GetProperty("EncryptedValue"));

            db.ChangeTracker.Clear();
            var get = await AssetEndpoints.GetAsync(assetId, db, user);
            AssertStatus(StatusCodes.Status200OK, get);
            var payload = Assert.IsAssignableFrom<IValueHttpResult>(get).Value!;
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            Assert.Equal(haloUrl, doc.RootElement.GetProperty("HaloAssetUrl").GetString());
            Assert.Equal(ninjaUrl, doc.RootElement.GetProperty("NinjaDeviceUrl").GetString());
            Assert.Equal(idsJson, doc.RootElement.GetProperty("ExternalIdsJson").GetString());

            var listed = await AssetEndpoints.ListAsync(db, user);
            AssertStatus(StatusCodes.Status200OK, listed);
            var listPayload = Assert.IsAssignableFrom<IValueHttpResult>(listed).Value!;
            using var listDoc = JsonDocument.Parse(JsonSerializer.Serialize(listPayload));
            var row = listDoc.RootElement.EnumerateArray().Single();
            Assert.Equal(haloUrl, row.GetProperty("HaloAssetUrl").GetString());
            Assert.Equal(ninjaUrl, row.GetProperty("NinjaDeviceUrl").GetString());
            Assert.False(row.TryGetProperty("ExternalIdsJson", out _));
        }

        var (updateDb, updateUser, _) = Open(dbName, tenantId);
        await using (updateDb)
        {
            var updatedHalo = "https://halo.example/asset/42/tickets";
            var updated = await AssetEndpoints.UpdateAsync(
                assetId,
                new UpdateAssetRequest(
                    null,
                    null,
                    null,
                    null,
                    null,
                    HaloAssetUrl: updatedHalo,
                    NinjaDeviceUrl: "",
                    ExternalIdsJson: CompanyIdentity.UpsertExternalId(null, CompanyIdentity.HaloKey, "halo-asset-42")),
                updateDb,
                updateUser);
            AssertStatus(StatusCodes.Status204NoContent, updated);

            updateDb.ChangeTracker.Clear();
            var reloaded = await updateDb.Assets.ForTenant(updateUser).AsNoTracking().SingleAsync(a => a.Id == assetId);
            Assert.Equal(updatedHalo, reloaded.HaloAssetUrl);
            Assert.Null(reloaded.NinjaDeviceUrl);
            Assert.Equal("halo-asset-42", CompanyIdentity.ReadExternalIds(reloaded.ExternalIdsJson)["halo"]);
            Assert.False(CompanyIdentity.ReadExternalIds(reloaded.ExternalIdsJson).ContainsKey("ninja"));
        }
    }

    [Fact]
    public async Task ForTenant_Does_Not_Leak_Other_Tenant_Asset_Deep_Links()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var typeA = await SeedTypeAsync(dbName, tenantA, "A-Type");
        var typeB = await SeedTypeAsync(dbName, tenantB, "B-Type");

        Guid assetBId;
        var (seedB, userB, authB) = Open(dbName, tenantB);
        await using (seedB)
        {
            var created = await AssetEndpoints.PostAsync(
                new CreateAssetRequest(
                    "Poison-Asset",
                    typeB,
                    null,
                    null,
                    null,
                    HaloAssetUrl: "https://halo.example/asset/secret",
                    NinjaDeviceUrl: "https://ninja.example/devices/secret",
                    ExternalIdsJson: """{"halo":"secret-asset"}"""),
                seedB,
                userB,
                authB);
            AssertStatus(StatusCodes.Status201Created, created);
            assetBId = (await seedB.Assets.ForTenant(userB).SingleAsync()).Id;
        }

        var (seedA, userA, authA) = Open(dbName, tenantA);
        await using (seedA)
        {
            var created = await AssetEndpoints.PostAsync(
                new CreateAssetRequest(
                    "Own-Asset",
                    typeA,
                    null,
                    null,
                    null,
                    HaloAssetUrl: "https://halo.example/asset/own",
                    NinjaDeviceUrl: "https://ninja.example/devices/own"),
                seedA,
                userA,
                authA);
            AssertStatus(StatusCodes.Status201Created, created);

            var listed = await seedA.Assets.ForTenant(userA).ToListAsync();
            Assert.Single(listed);
            Assert.Equal("Own-Asset", listed[0].Name);
            Assert.Equal("https://halo.example/asset/own", listed[0].HaloAssetUrl);
            Assert.DoesNotContain(listed, a => a.Id == assetBId);
            Assert.DoesNotContain(listed, a => a.HaloAssetUrl != null && a.HaloAssetUrl.Contains("secret", StringComparison.Ordinal));
            Assert.DoesNotContain(listed, a => a.NinjaDeviceUrl != null && a.NinjaDeviceUrl.Contains("secret", StringComparison.Ordinal));
            Assert.DoesNotContain(listed, a => a.ExternalIdsJson != null && a.ExternalIdsJson.Contains("secret", StringComparison.Ordinal));

            var hidden = await seedA.Assets.ForTenant(userA).FirstOrDefaultAsync(a => a.Id == assetBId);
            Assert.Null(hidden);

            var getHidden = await AssetEndpoints.GetAsync(assetBId, seedA, userA);
            AssertStatus(StatusCodes.Status404NotFound, getHidden);

            var updateHidden = await AssetEndpoints.UpdateAsync(
                assetBId,
                new UpdateAssetRequest(null, null, null, null, null, HaloAssetUrl: "https://halo.example/stolen"),
                seedA,
                userA);
            AssertStatus(StatusCodes.Status404NotFound, updateHidden);

            var list = await AssetEndpoints.ListAsync(seedA, userA);
            var listJson = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(list).Value!);
            Assert.Contains("https://halo.example/asset/own", listJson);
            Assert.DoesNotContain("secret", listJson, StringComparison.Ordinal);
        }

        var (verifyB, verifyUserB, _) = Open(dbName, tenantB);
        await using (verifyB)
        {
            var stillB = await verifyB.Assets.ForTenant(verifyUserB).AsNoTracking().SingleAsync(a => a.Id == assetBId);
            Assert.Equal("https://halo.example/asset/secret", stillB.HaloAssetUrl);
            Assert.Equal("https://ninja.example/devices/secret", stillB.NinjaDeviceUrl);
            Assert.Equal("secret-asset", CompanyIdentity.ReadExternalIds(stillB.ExternalIdsJson)["halo"]);
        }
    }

    [Fact]
    public async Task Null_Deep_Link_Fields_On_Update_Leave_Stored_Values_Unchanged()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var typeId = await SeedTypeAsync(dbName, tenantId);

        Guid assetId;
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var created = await AssetEndpoints.PostAsync(
                new CreateAssetRequest(
                    "Switch",
                    typeId,
                    null,
                    null,
                    null,
                    HaloAssetUrl: "https://halo.example/asset/7",
                    NinjaDeviceUrl: "https://ninja.example/devices/7",
                    ExternalIdsJson: """{"halo":"7"}"""),
                db,
                user,
                auth);
            AssertStatus(StatusCodes.Status201Created, created);
            assetId = (await db.Assets.ForTenant(user).SingleAsync()).Id;

            var renamed = await AssetEndpoints.UpdateAsync(
                assetId,
                new UpdateAssetRequest("Core switch", null, null, null, null),
                db,
                user);
            AssertStatus(StatusCodes.Status204NoContent, renamed);

            db.ChangeTracker.Clear();
            var stored = await db.Assets.ForTenant(user).AsNoTracking().SingleAsync(a => a.Id == assetId);
            Assert.Equal("Core switch", stored.Name);
            Assert.Equal("https://halo.example/asset/7", stored.HaloAssetUrl);
            Assert.Equal("https://ninja.example/devices/7", stored.NinjaDeviceUrl);
            Assert.Equal("7", CompanyIdentity.ReadExternalIds(stored.ExternalIdsJson)["halo"]);
        }
    }
}

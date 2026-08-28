using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class ExpirationTests
{
    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
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
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static async Task<(Guid TenantA, Guid TenantB, Guid CompanyA, Guid CompanyB, Guid CompanyAOnly, string DbName)> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantA, Name = "ExampleCo", Slug = "exampleco" };
        var companyAOnly = new Company { TenantId = tenantA, Name = "OtherCo", Slug = "otherco" };
        var companyB = new Company { TenantId = tenantB, Name = "PoisonCo", Slug = "poisonco" };

        var typeA = new AssetType
        {
            TenantId = tenantA,
            Name = "Licenses",
            Fields =
            [
                new FieldDefinition { Name = "License Expiration", FieldType = "Date", IsExpiration = true, SortOrder = 0 },
                new FieldDefinition { Name = "End Date", FieldType = "DateTime", IsExpiration = true, SortOrder = 1 },
                new FieldDefinition { Name = "Notes", FieldType = "Text", IsExpiration = false, SortOrder = 2 },
                new FieldDefinition { Name = "Installed", FieldType = "Date", IsExpiration = false, SortOrder = 3 },
            ],
        };
        var typeB = new AssetType
        {
            TenantId = tenantB,
            Name = "SSL Certs",
            Fields =
            [
                new FieldDefinition { Name = "SSL Certificate", FieldType = "Date", IsExpiration = true, SortOrder = 0 },
            ],
        };

        var future = DateTimeOffset.UtcNow.AddDays(30);
        var soon = DateTimeOffset.UtcNow.AddDays(4);
        var past = DateTimeOffset.UtcNow.AddDays(-12);

        var (dbA, _) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Companies.AddRange(companyA, companyAOnly);
            dbA.AssetTypes.Add(typeA);
            await dbA.SaveChangesAsync();

            var licenseField = typeA.Fields.Single(f => f.Name == "License Expiration");
            var endField = typeA.Fields.Single(f => f.Name == "End Date");
            var notesField = typeA.Fields.Single(f => f.Name == "Notes");
            var installedField = typeA.Fields.Single(f => f.Name == "Installed");

            var office = new Asset
            {
                TenantId = tenantA,
                Name = "Office 365",
                AssetTypeId = typeA.Id,
                CompanyId = companyA.Id,
            };
            office.CustomFieldValues.Add(new CustomFieldValue
            {
                FieldDefinitionId = licenseField.Id,
                Value = future.ToString("O"),
            });
            office.CustomFieldValues.Add(new CustomFieldValue
            {
                FieldDefinitionId = notesField.Id,
                Value = "not a date field marked expiration",
            });
            office.CustomFieldValues.Add(new CustomFieldValue
            {
                FieldDefinitionId = installedField.Id,
                Value = past.ToString("O"),
            });

            var contract = new Asset
            {
                TenantId = tenantA,
                Name = "Firewall contract",
                AssetTypeId = typeA.Id,
                CompanyId = companyA.Id,
                ExpiresAt = past,
            };
            contract.CustomFieldValues.Add(new CustomFieldValue
            {
                FieldDefinitionId = endField.Id,
                Value = soon.UtcDateTime.ToString("yyyy-MM-dd"),
            });

            var ups = new Asset
            {
                TenantId = tenantA,
                Name = "UPS battery",
                AssetTypeId = typeA.Id,
                CompanyId = companyAOnly.Id,
                ExpiresAt = future,
            };

            dbA.Assets.AddRange(office, contract, ups);
            await dbA.SaveChangesAsync();
        }

        var (dbB, _) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Companies.Add(companyB);
            dbB.AssetTypes.Add(typeB);
            await dbB.SaveChangesAsync();

            var sslField = typeB.Fields.Single();
            var cert = new Asset
            {
                TenantId = tenantB,
                Name = "Poison SSL",
                AssetTypeId = typeB.Id,
                CompanyId = companyB.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            };
            cert.CustomFieldValues.Add(new CustomFieldValue
            {
                FieldDefinitionId = sslField.Id,
                Value = DateTimeOffset.UtcNow.AddDays(2).ToString("O"),
            });
            dbB.Assets.Add(cert);
            await dbB.SaveChangesAsync();
        }

        return (tenantA, tenantB, companyA.Id, companyB.Id, companyAOnly.Id, dbName);
    }

    [Fact]
    public async Task Query_Does_Not_Leak_Other_Tenant_Expirations()
    {
        var (tenantA, _, _, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var items = await ExpirationEndpoints.QueryAsync(db, user, showExpired: true);
            Assert.NotEmpty(items);
            Assert.DoesNotContain(items, i => i.Name.Contains("Poison", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(items, i => i.CompanyName == "PoisonCo");
            Assert.All(items, i => Assert.NotEqual("SSL Certificate", i.FieldName));
        }
    }

    [Fact]
    public async Task Query_Hides_Expired_Unless_ShowExpired()
    {
        var (tenantA, _, _, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var hidden = await ExpirationEndpoints.QueryAsync(db, user, showExpired: false);
            Assert.DoesNotContain(hidden, i => i.DaysUntil < 0);
            Assert.Contains(hidden, i => i.Name == "Office 365" && i.FieldName == "License Expiration");
            Assert.Contains(hidden, i => i.Name == "Firewall contract" && i.SourceType == ExpirationEndpoints.SourceAssetField);
            Assert.Contains(hidden, i => i.Name == "UPS battery" && i.SourceType == ExpirationEndpoints.SourceAsset);
            Assert.DoesNotContain(hidden, i => i.Name == "Firewall contract" && i.SourceType == ExpirationEndpoints.SourceAsset);

            var shown = await ExpirationEndpoints.QueryAsync(db, user, showExpired: true);
            Assert.Contains(shown, i => i.Name == "Firewall contract" && i.SourceType == ExpirationEndpoints.SourceAsset && i.DaysUntil < 0);
            Assert.True(shown.Count > hidden.Count);
            Assert.Equal(shown.OrderBy(i => i.ExpiresAt).Select(i => (i.Id, i.SourceType)), shown.Select(i => (i.Id, i.SourceType)));
        }
    }

    [Fact]
    public async Task Query_CompanyId_Uses_ForTenant_And_Does_Not_Five_Hundred()
    {
        var (tenantA, _, companyA, companyB, companyAOnly, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var otherTenant = await ExpirationEndpoints.QueryAsync(db, user, companyId: companyB, showExpired: true);
            Assert.Empty(otherTenant);

            var missing = await ExpirationEndpoints.QueryAsync(db, user, companyId: Guid.NewGuid(), showExpired: true);
            Assert.Empty(missing);

            var scoped = await ExpirationEndpoints.QueryAsync(db, user, companyId: companyA, showExpired: true);
            Assert.NotEmpty(scoped);
            Assert.All(scoped, i => Assert.Equal(companyA, i.CompanyId));
            Assert.DoesNotContain(scoped, i => i.Name == "UPS battery");
            Assert.Contains(scoped, i => i.CompanyName == "ExampleCo");

            var otherCo = await ExpirationEndpoints.QueryAsync(db, user, companyId: companyAOnly, showExpired: false);
            Assert.Single(otherCo);
            Assert.Equal("UPS battery", otherCo[0].Name);
        }
    }

    [Fact]
    public async Task Query_Ignores_Non_Expiration_Date_Fields_And_Accepts_ExpiresAt()
    {
        var (tenantA, _, _, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var items = await ExpirationEndpoints.QueryAsync(db, user, showExpired: true);
            Assert.DoesNotContain(items, i => i.FieldName == "Notes");
            Assert.DoesNotContain(items, i => i.FieldName == "Installed");
            Assert.Contains(items, i => i.SourceType == ExpirationEndpoints.SourceAsset && i.FieldName == "Expiration");
            Assert.Contains(items, i => i.SourceType == ExpirationEndpoints.SourceAssetField && i.FieldName == "License Expiration");
            Assert.Contains(items, i => i.FieldName == "End Date");
        }
    }

    [Fact]
    public async Task Create_And_Update_Accept_IsExpiration_And_ExpiresAt()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId);
        await using (db)
        {
            var type = new AssetType
            {
                TenantId = tenantId,
                Name = "Domains",
                Fields =
                [
                    new FieldDefinition { Name = "Domain", FieldType = "Date", IsExpiration = true },
                    new FieldDefinition { Name = "Renewal Date", FieldType = "Date", IsExpiration = false },
                ],
            };
            db.AssetTypes.Add(type);
            await db.SaveChangesAsync();

            var renewal = type.Fields.Single(f => f.Name == "Renewal Date");
            renewal.IsExpiration = true;
            await db.SaveChangesAsync();

            var when = DateTimeOffset.UtcNow.AddDays(90);
            var asset = new Asset
            {
                TenantId = tenantId,
                Name = "example.com",
                AssetTypeId = type.Id,
                ExpiresAt = when,
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();
            var loadedType = await db.AssetTypes.ForTenant(user).Include(t => t.Fields).SingleAsync();
            Assert.All(loadedType.Fields, f => Assert.True(f.IsExpiration));

            var loaded = await db.Assets.ForTenant(user).SingleAsync();
            Assert.Equal(when, loaded.ExpiresAt);

            loaded.ExpiresAt = when.AddDays(10);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var updated = await db.Assets.ForTenant(user).SingleAsync();
            Assert.Equal(when.AddDays(10), updated.ExpiresAt);
        }
    }
}

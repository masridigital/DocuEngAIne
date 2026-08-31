using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Api.Mcp;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class OutboundMcpTests
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

    private static async Task<(
        string DbName,
        Guid TenantA,
        Guid TenantB,
        Guid CompanyA,
        Guid CompanyB,
        Guid AssetA,
        Guid DocA,
        Guid RunbookA,
        Guid KeeperA)> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantA, Name = "ExampleCo", Slug = "exampleco" };
        var companyB = new Company { TenantId = tenantB, Name = "PoisonCo", Slug = "poisonco" };
        var typeA = new AssetType { TenantId = tenantA, Name = "Servers" };
        var typeB = new AssetType { TenantId = tenantB, Name = "PoisonType" };

        var (dbA, _) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Tenants.Add(new Tenant { Id = tenantA, Name = "A", Slug = "a" });
            dbA.Companies.Add(companyA);
            dbA.AssetTypes.Add(typeA);
            await dbA.SaveChangesAsync();

            dbA.Assets.Add(new Asset
            {
                TenantId = tenantA,
                Name = "A-Server",
                AssetTypeId = typeA.Id,
                CompanyId = companyA.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
            });
            dbA.Documents.Add(new Document
            {
                TenantId = tenantA,
                Title = "A-Doc",
                Slug = "a-doc",
                Summary = "ours",
                CompanyId = companyA.Id,
                IsPublished = true,
            });
            dbA.Runbooks.Add(new Runbook
            {
                TenantId = tenantA,
                Title = "A-SOP",
                Slug = "a-sop",
                CompanyId = companyA.Id,
                IsPublished = true,
            });
            dbA.KeeperLinks.Add(new KeeperLink
            {
                TenantId = tenantA,
                Name = "A-Vault",
                KeeperRecordUrl = "https://keeper.example/a",
                UsernameHint = "admin-a",
                Notes = "do-not-leak-notes",
                CompanyId = companyA.Id,
            });
            await dbA.SaveChangesAsync();
        }

        var (dbB, _) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Tenants.Add(new Tenant { Id = tenantB, Name = "B", Slug = "b" });
            dbB.Companies.Add(companyB);
            dbB.AssetTypes.Add(typeB);
            await dbB.SaveChangesAsync();

            dbB.Assets.Add(new Asset
            {
                TenantId = tenantB,
                Name = "Poison-Server",
                AssetTypeId = typeB.Id,
                CompanyId = companyB.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(3),
            });
            dbB.Documents.Add(new Document
            {
                TenantId = tenantB,
                Title = "Poison-Doc",
                Slug = "poison-doc",
                CompanyId = companyB.Id,
                IsPublished = true,
            });
            dbB.Runbooks.Add(new Runbook
            {
                TenantId = tenantB,
                Title = "Poison-SOP",
                Slug = "poison-sop",
                CompanyId = companyB.Id,
                IsPublished = true,
            });
            dbB.KeeperLinks.Add(new KeeperLink
            {
                TenantId = tenantB,
                Name = "Poison-Vault",
                KeeperRecordUrl = "https://keeper.example/poison",
                Notes = "secret-b",
                CompanyId = companyB.Id,
            });
            await dbB.SaveChangesAsync();
        }

        var (ids, _) = Open(dbName, tenantA);
        await using (ids)
        {
            var assetA = await ids.Assets.SingleAsync(a => a.Name == "A-Server");
            var docA = await ids.Documents.SingleAsync(d => d.Title == "A-Doc");
            var runbookA = await ids.Runbooks.SingleAsync(r => r.Title == "A-SOP");
            var keeperA = await ids.KeeperLinks.SingleAsync(k => k.Name == "A-Vault");
            return (dbName, tenantA, tenantB, companyA.Id, companyB.Id, assetA.Id, docA.Id, runbookA.Id, keeperA.Id);
        }
    }

    private static TokenCurrentUser TokenUser(Guid tenantId) =>
        new(tenantId, Guid.NewGuid(), "mcp-test");

    private static McpJsonRpcRequest Rpc(string method, object? @params = null, string? id = "1")
    {
        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params,
        }, DocuEngAIneMcpServer.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        return DocuEngAIneMcpServer.Parse(doc.RootElement.Clone());
    }

    private static string ResultText(McpJsonRpcResponse response)
    {
        var json = JsonSerializer.Serialize(response.Result, DocuEngAIneMcpServer.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array
            && content.GetArrayLength() > 0)
        {
            return content[0].GetProperty("text").GetString() ?? json;
        }

        return json;
    }

    [Fact]
    public void Tools_List_Is_Read_Only_And_Does_Not_Expose_Reveal()
    {
        var names = DocuEngAIneMcpServer.Tools.Select(t => t.Name).ToArray();
        Assert.Equal(
            new[]
            {
                DocuEngAIneMcpServer.ListCompanies,
                DocuEngAIneMcpServer.GetCompany,
                DocuEngAIneMcpServer.ListAssets,
                DocuEngAIneMcpServer.GetAsset,
                DocuEngAIneMcpServer.ListDocuments,
                DocuEngAIneMcpServer.ListRunbooks,
                DocuEngAIneMcpServer.ListExpirations,
                DocuEngAIneMcpServer.ListKeeperLinks,
            },
            names);

        Assert.DoesNotContain(names, n => n.Contains("reveal", StringComparison.OrdinalIgnoreCase));
        Assert.False(DocuEngAIneMcpServer.IsKnownTool("reveal"));
        Assert.False(DocuEngAIneMcpServer.IsKnownTool("keeper_reveal"));
        Assert.False(DocuEngAIneMcpServer.IsKnownTool("reveal_keeper_link"));
    }

    [Fact]
    public async Task Call_Reveal_Is_Rejected_As_Unknown_Tool()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var user = TokenUser(seed.TenantA);
            foreach (var name in new[] { "reveal", "keeper_reveal", "reveal_keeper", "KeeperLink.Reveal" })
            {
                var response = await DocuEngAIneMcpServer.HandleAsync(
                    Rpc("tools/call", new { name, arguments = new { id = seed.KeeperA } }),
                    db,
                    user);

                Assert.NotNull(response.Error);
                var errorJson = JsonSerializer.Serialize(response.Error, DocuEngAIneMcpServer.JsonOptions);
                Assert.Contains("Tool not found", errorJson);
                Assert.Contains(name, errorJson);
            }

            Assert.Empty(await db.AuditLogs.Where(a => a.Action == "KeeperLink.Reveal").ToListAsync());
        }
    }

    [Fact]
    public async Task ForTenant_Isolation_On_All_List_Tools()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var user = TokenUser(seed.TenantA);

            var companies = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.ListCompanies, null, db, user));
            Assert.Contains("ExampleCo", companies);
            Assert.DoesNotContain("PoisonCo", companies);

            var assets = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.ListAssets, null, db, user));
            Assert.Contains("A-Server", assets);
            Assert.DoesNotContain("Poison-Server", assets);

            var docs = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.ListDocuments, null, db, user));
            Assert.Contains("A-Doc", docs);
            Assert.DoesNotContain("Poison-Doc", docs);

            var runbooks = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.ListRunbooks, null, db, user));
            Assert.Contains("A-SOP", runbooks);
            Assert.DoesNotContain("Poison-SOP", runbooks);

            var expirations = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.ListExpirations, null, db, user));
            Assert.Contains("A-Server", expirations);
            Assert.DoesNotContain("Poison-Server", expirations);

            var keepers = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.ListKeeperLinks, null, db, user));
            Assert.Contains("A-Vault", keepers);
            Assert.Contains("https://keeper.example/a", keepers);
            Assert.DoesNotContain("Poison-Vault", keepers);
            Assert.DoesNotContain("keeper.example/poison", keepers);
        }
    }

    [Fact]
    public async Task Get_Company_And_Company_Filters_Do_Not_Leak_Other_Tenant()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var user = TokenUser(seed.TenantA);
            var ownArgs = JsonSerializer.SerializeToElement(new { companyId = seed.CompanyA.ToString() });
            var foreignArgs = JsonSerializer.SerializeToElement(new { companyId = seed.CompanyB.ToString() });

            var own = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.GetCompany, ownArgs, db, user));
            Assert.Contains("ExampleCo", own);
            Assert.DoesNotContain("PoisonCo", own);

            var missing = await Assert.ThrowsAsync<McpToolException>(() =>
                DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.GetCompany, foreignArgs, db, user));
            Assert.Equal("Company not found.", missing.Message);

            foreach (var tool in new[]
            {
                DocuEngAIneMcpServer.ListAssets,
                DocuEngAIneMcpServer.ListDocuments,
                DocuEngAIneMcpServer.ListRunbooks,
                DocuEngAIneMcpServer.ListExpirations,
                DocuEngAIneMcpServer.ListKeeperLinks,
            })
            {
                var json = JsonSerializer.Serialize(
                    await DocuEngAIneMcpServer.InvokeToolAsync(tool, foreignArgs, db, user));
                Assert.DoesNotContain("Poison", json);
                Assert.DoesNotContain(seed.CompanyB.ToString(), json);
            }
        }
    }

    [Fact]
    public async Task List_Keeper_Links_Returns_Url_And_Title_Only_Without_Reveal_Audit()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var user = TokenUser(seed.TenantA);
            var items = await DocuEngAIneMcpServer.InvokeToolAsync(
                DocuEngAIneMcpServer.ListKeeperLinks, null, db, user);
            var json = JsonSerializer.Serialize(items, DocuEngAIneMcpServer.JsonOptions);

            Assert.Contains("A-Vault", json);
            Assert.Contains("https://keeper.example/a", json);
            Assert.DoesNotContain("admin-a", json);
            Assert.DoesNotContain("do-not-leak-notes", json);
            Assert.Empty(await db.AuditLogs.Where(a => a.Action == "KeeperLink.Reveal").ToListAsync());
        }
    }

    [Fact]
    public async Task Handle_Initialize_And_Tools_List_And_Call()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var user = TokenUser(seed.TenantA);

            var init = await DocuEngAIneMcpServer.HandleAsync(Rpc("initialize", new { protocolVersion = "2025-06-18" }), db, user);
            var initJson = JsonSerializer.Serialize(init, DocuEngAIneMcpServer.JsonOptions);
            Assert.Contains(DocuEngAIneMcpServer.ProtocolVersion, initJson);
            Assert.Contains(DocuEngAIneMcpServer.ServerName, initJson);

            var list = await DocuEngAIneMcpServer.HandleAsync(Rpc("tools/list"), db, user);
            var listJson = JsonSerializer.Serialize(list, DocuEngAIneMcpServer.JsonOptions);
            Assert.Contains(DocuEngAIneMcpServer.ListCompanies, listJson);
            Assert.Contains(DocuEngAIneMcpServer.ListAssets, listJson);
            Assert.Contains(DocuEngAIneMcpServer.GetAsset, listJson);
            Assert.Contains(DocuEngAIneMcpServer.ListExpirations, listJson);
            Assert.DoesNotContain(DocuEngAIneMcpServer.Tools, t => t.Name.Contains("reveal", StringComparison.OrdinalIgnoreCase));

            var call = await DocuEngAIneMcpServer.HandleAsync(
                Rpc("tools/call", new { name = DocuEngAIneMcpServer.ListCompanies, arguments = new { } }),
                db,
                user);
            Assert.Null(call.Error);
            Assert.Contains("ExampleCo", ResultText(call));
            Assert.DoesNotContain("PoisonCo", ResultText(call));

            var assetsCall = await DocuEngAIneMcpServer.HandleAsync(
                Rpc("tools/call", new { name = DocuEngAIneMcpServer.ListAssets, arguments = new { } }),
                db,
                user);
            Assert.Null(assetsCall.Error);
            Assert.Contains("A-Server", ResultText(assetsCall));
            Assert.DoesNotContain("Poison-Server", ResultText(assetsCall));

            var expirationsCall = await DocuEngAIneMcpServer.HandleAsync(
                Rpc("tools/call", new { name = DocuEngAIneMcpServer.ListExpirations, arguments = new { } }),
                db,
                user);
            Assert.Null(expirationsCall.Error);
            Assert.Contains("A-Server", ResultText(expirationsCall));
            Assert.DoesNotContain("Poison-Server", ResultText(expirationsCall));
        }
    }

    [Fact]
    public void Get_Mcp_Documentation_Names_Tools_And_Token_Auth()
    {
        var json = JsonSerializer.Serialize(OutboundMcpEndpoints.Describe(), DocuEngAIneMcpServer.JsonOptions);
        Assert.Contains("/mcp", json);
        Assert.Contains("apiToken", json);
        Assert.Contains(DocuEngAIneMcpServer.ListAssets, json);
        Assert.Contains(DocuEngAIneMcpServer.GetAsset, json);
        Assert.Contains(DocuEngAIneMcpServer.ListExpirations, json);
        Assert.Contains(DocuEngAIneMcpServer.ListKeeperLinks, json);
        using (var doc = JsonDocument.Parse(json))
        {
            var tools = doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToArray();
            Assert.DoesNotContain(tools, n => n is not null && n.Contains("reveal", StringComparison.OrdinalIgnoreCase));
        }
        Assert.Contains("ForTenant", json);
    }

    [Fact]
    public async Task Get_Asset_Is_ForTenant_And_Returns_Fields()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var type = await db.AssetTypes.SingleAsync(t => t.TenantId == seed.TenantA);
            var warranty = new FieldDefinition
            {
                AssetTypeId = type.Id,
                Name = "Warranty",
                FieldType = "Date",
                IsExpiration = true,
            };
            db.FieldDefinitions.Add(warranty);
            await db.SaveChangesAsync();
            db.CustomFieldValues.Add(new CustomFieldValue
            {
                AssetId = seed.AssetA,
                FieldDefinitionId = warranty.Id,
                Value = "2027-03-01",
            });
            await db.SaveChangesAsync();

            var user = TokenUser(seed.TenantA);
            var ownArgs = JsonSerializer.SerializeToElement(new { assetId = seed.AssetA.ToString() });
            var foreignArgs = JsonSerializer.SerializeToElement(new { assetId = (await db.Assets.SingleAsync(a => a.Name == "Poison-Server")).Id.ToString() });

            var own = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.GetAsset, ownArgs, db, user));
            Assert.Contains("A-Server", own);
            Assert.Contains("Warranty", own);
            Assert.Contains("2027-03-01", own);
            Assert.DoesNotContain("Poison-Server", own);

            var missing = await Assert.ThrowsAsync<McpToolException>(() =>
                DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.GetAsset, foreignArgs, db, user));
            Assert.Equal("Asset not found.", missing.Message);
        }
    }

    [Fact]
    public async Task List_Assets_Filters_By_Company_And_Name_Without_Cross_Tenant()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var type = await db.AssetTypes.SingleAsync(t => t.TenantId == seed.TenantA);
            var other = new Company { TenantId = seed.TenantA, Name = "FilterCo", Slug = "filterco" };
            db.Companies.Add(other);
            await db.SaveChangesAsync();
            db.Assets.Add(new Asset
            {
                TenantId = seed.TenantA,
                Name = "Filter-Laptop",
                AssetTypeId = type.Id,
                CompanyId = other.Id,
            });
            await db.SaveChangesAsync();

            var user = TokenUser(seed.TenantA);
            var byCompany = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.ListAssets,
                    JsonSerializer.SerializeToElement(new { companyId = seed.CompanyA.ToString() }),
                    db,
                    user));
            Assert.Contains("A-Server", byCompany);
            Assert.DoesNotContain("Filter-Laptop", byCompany);
            Assert.DoesNotContain("Poison-Server", byCompany);

            var byName = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.ListAssets,
                    JsonSerializer.SerializeToElement(new { q = "Laptop" }),
                    db,
                    user));
            Assert.Contains("Filter-Laptop", byName);
            Assert.DoesNotContain("A-Server", byName);
            Assert.DoesNotContain("Poison-Server", byName);
        }
    }

    [Fact]
    public async Task List_Expirations_Filters_ShowExpired_Search_And_Company()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var type = await db.AssetTypes.SingleAsync(t => t.TenantId == seed.TenantA);
            db.Assets.Add(new Asset
            {
                TenantId = seed.TenantA,
                Name = "A-Expired",
                AssetTypeId = type.Id,
                CompanyId = seed.CompanyA,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-5),
            });
            await db.SaveChangesAsync();

            var user = TokenUser(seed.TenantA);

            var upcoming = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(DocuEngAIneMcpServer.ListExpirations, null, db, user));
            Assert.Contains("A-Server", upcoming);
            Assert.DoesNotContain("A-Expired", upcoming);
            Assert.DoesNotContain("Poison-Server", upcoming);

            var includingPast = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.ListExpirations,
                    JsonSerializer.SerializeToElement(new { showExpired = true }),
                    db,
                    user));
            Assert.Contains("A-Server", includingPast);
            Assert.Contains("A-Expired", includingPast);
            Assert.DoesNotContain("Poison-Server", includingPast);

            var byName = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.ListExpirations,
                    JsonSerializer.SerializeToElement(new { showExpired = true, q = "Expired" }),
                    db,
                    user));
            Assert.Contains("A-Expired", byName);
            Assert.DoesNotContain("A-Server", byName);

            var otherCompany = new Company { TenantId = seed.TenantA, Name = "NoExpireCo", Slug = "noexpireco" };
            db.Companies.Add(otherCompany);
            await db.SaveChangesAsync();
            var emptyCompany = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.ListExpirations,
                    JsonSerializer.SerializeToElement(new { companyId = otherCompany.Id.ToString(), showExpired = true }),
                    db,
                    user));
            Assert.DoesNotContain("A-Server", emptyCompany);
            Assert.DoesNotContain("A-Expired", emptyCompany);
            Assert.DoesNotContain("Poison-Server", emptyCompany);
        }
    }

    [Fact]
    public async Task List_Companies_Search_Is_ForTenant()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            db.Companies.Add(new Company { TenantId = seed.TenantA, Name = "Alpha Widgets", Slug = "alpha-widgets" });
            await db.SaveChangesAsync();

            var user = TokenUser(seed.TenantA);
            var json = JsonSerializer.Serialize(
                await DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.ListCompanies,
                    JsonSerializer.SerializeToElement(new { q = "Alpha" }),
                    db,
                    user));
            Assert.Contains("Alpha Widgets", json);
            Assert.DoesNotContain("ExampleCo", json);
            Assert.DoesNotContain("PoisonCo", json);
        }
    }

    [Fact]
    public void Event_Stream_Only_Accept_Is_Detected()
    {
        Assert.False(OutboundMcpEndpoints.WantsEventStreamOnly(null));
        Assert.False(OutboundMcpEndpoints.WantsEventStreamOnly("application/json, text/event-stream"));
        Assert.True(OutboundMcpEndpoints.WantsEventStreamOnly("text/event-stream"));
    }
}

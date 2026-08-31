using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Api.Mcp;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class OutboundMcpTests
{
    private sealed class RecordingAudit : IAuditService
    {
        public List<(string Action, string EntityType, Guid? EntityId, string? Details)> Entries { get; } = [];

        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add((action, entityType, entityId, details));
            return Task.CompletedTask;
        }
    }

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
    public void Tools_List_Exposes_Single_Audited_Reveal_Only()
    {
        var names = DocuEngAIneMcpServer.Tools.Select(t => t.Name).ToArray();
        Assert.Equal(
            new[]
            {
                DocuEngAIneMcpServer.ListCompanies,
                DocuEngAIneMcpServer.GetCompany,
                DocuEngAIneMcpServer.ListAssets,
                DocuEngAIneMcpServer.ListDocuments,
                DocuEngAIneMcpServer.ListRunbooks,
                DocuEngAIneMcpServer.ListExpirations,
                DocuEngAIneMcpServer.ListKeeperLinks,
                DocuEngAIneMcpServer.RevealKeeperLink,
            },
            names);

        Assert.True(DocuEngAIneMcpServer.IsKnownTool(DocuEngAIneMcpServer.RevealKeeperLink));
        Assert.False(DocuEngAIneMcpServer.IsKnownTool("reveal"));
        Assert.False(DocuEngAIneMcpServer.IsKnownTool("keeper_reveal"));
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
            Assert.DoesNotContain("keeper.example", keepers);
            Assert.DoesNotContain("Poison-Vault", keepers);
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
    public async Task List_Keeper_Links_Returns_Titles_And_Ids_Only_Never_Urls()
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
            Assert.Contains(seed.KeeperA.ToString(), json);
            Assert.DoesNotContain("keeper.example", json);
            Assert.DoesNotContain("admin-a", json);
            Assert.DoesNotContain("do-not-leak-notes", json);
            Assert.Empty(await db.AuditLogs.Where(a => a.Action == "KeeperLink.Reveal").ToListAsync());
        }
    }

    [Fact]
    public async Task Reveal_Keeper_Link_Returns_One_Url_And_Writes_Audit_Row()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var user = TokenUser(seed.TenantA);
            var audit = new RecordingAudit();
            var args = JsonSerializer.SerializeToElement(new { keeperLinkId = seed.KeeperA.ToString() });

            var result = await DocuEngAIneMcpServer.InvokeToolAsync(
                DocuEngAIneMcpServer.RevealKeeperLink, args, db, user, audit);
            var json = JsonSerializer.Serialize(result, DocuEngAIneMcpServer.JsonOptions);

            Assert.Contains("https://keeper.example/a", json);
            Assert.Contains("A-Vault", json);

            var entry = Assert.Single(audit.Entries);
            Assert.Equal("KeeperLink.Reveal", entry.Action);
            Assert.Equal(nameof(KeeperLink), entry.EntityType);
            Assert.Equal(seed.KeeperA, entry.EntityId);
        }
    }

    [Fact]
    public async Task Reveal_Keeper_Link_Foreign_Tenant_Is_Not_Found_And_Not_Audited()
    {
        var seed = await SeedAsync();
        Guid keeperB;
        var (dbB, _) = Open(seed.DbName, seed.TenantB);
        await using (dbB)
        {
            keeperB = (await dbB.KeeperLinks.SingleAsync(k => k.Name == "Poison-Vault")).Id;
        }

        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var user = TokenUser(seed.TenantA);
            var audit = new RecordingAudit();
            var args = JsonSerializer.SerializeToElement(new { keeperLinkId = keeperB.ToString() });

            var ex = await Assert.ThrowsAsync<McpToolException>(() =>
                DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.RevealKeeperLink, args, db, user, audit));

            Assert.Equal("Keeper link not found.", ex.Message);
            Assert.Empty(audit.Entries);
        }
    }

    [Fact]
    public async Task Reveal_Keeper_Link_Fails_Closed_Without_Audit_Sink()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var user = TokenUser(seed.TenantA);
            var args = JsonSerializer.SerializeToElement(new { keeperLinkId = seed.KeeperA.ToString() });

            var ex = await Assert.ThrowsAsync<McpToolException>(() =>
                DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.RevealKeeperLink, args, db, user));

            Assert.Contains("audit", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Reveal_Keeper_Link_Requires_Id_And_Configured_Url()
    {
        var seed = await SeedAsync();
        var (db, _) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var user = TokenUser(seed.TenantA);
            var audit = new RecordingAudit();

            var missingId = await Assert.ThrowsAsync<McpToolException>(() =>
                DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.RevealKeeperLink, null, db, user, audit));
            Assert.Equal("keeperLinkId is required.", missingId.Message);

            db.KeeperLinks.Add(new KeeperLink
            {
                TenantId = seed.TenantA,
                Name = "A-NoUrl",
                CompanyId = seed.CompanyA,
            });
            await db.SaveChangesAsync();
            var noUrl = await db.KeeperLinks.SingleAsync(k => k.Name == "A-NoUrl");

            var args = JsonSerializer.SerializeToElement(new { keeperLinkId = noUrl.Id.ToString() });
            var noUrlEx = await Assert.ThrowsAsync<McpToolException>(() =>
                DocuEngAIneMcpServer.InvokeToolAsync(
                    DocuEngAIneMcpServer.RevealKeeperLink, args, db, user, audit));
            Assert.Equal("No Keeper URL configured for this link.", noUrlEx.Message);

            Assert.Empty(audit.Entries);
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
            Assert.Contains(DocuEngAIneMcpServer.RevealKeeperLink, listJson);

            var call = await DocuEngAIneMcpServer.HandleAsync(
                Rpc("tools/call", new { name = DocuEngAIneMcpServer.ListCompanies, arguments = new { } }),
                db,
                user);
            Assert.Null(call.Error);
            Assert.Contains("ExampleCo", ResultText(call));
            Assert.DoesNotContain("PoisonCo", ResultText(call));
        }
    }

    [Fact]
    public void Get_Mcp_Documentation_Names_Tools_And_Token_Auth()
    {
        var json = JsonSerializer.Serialize(OutboundMcpEndpoints.Describe(), DocuEngAIneMcpServer.JsonOptions);
        Assert.Contains("/mcp", json);
        Assert.Contains("apiToken", json);
        Assert.Contains(DocuEngAIneMcpServer.ListKeeperLinks, json);
        using (var doc = JsonDocument.Parse(json))
        {
            var tools = doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToArray();
            Assert.Contains(DocuEngAIneMcpServer.RevealKeeperLink, tools);
        }
        Assert.Contains("ForTenant", json);
    }

    [Fact]
    public void Event_Stream_Only_Accept_Is_Detected()
    {
        Assert.False(OutboundMcpEndpoints.WantsEventStreamOnly(null));
        Assert.False(OutboundMcpEndpoints.WantsEventStreamOnly("application/json, text/event-stream"));
        Assert.True(OutboundMcpEndpoints.WantsEventStreamOnly("text/event-stream"));
    }
}

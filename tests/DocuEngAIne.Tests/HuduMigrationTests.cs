using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Integrations;
using DocuEngAIne.Infrastructure.Integrations.Migration;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class HuduMigrationTests
{
    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingHuduMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public IReadOnlyList<string> Tools { get; init; } =
        [
            McpServerDefaults.HuduListCompaniesTool,
            McpServerDefaults.HuduListArticlesTool,
            McpServerDefaults.HuduListFoldersTool,
            McpServerDefaults.HuduGetArticleTool,
            "hudu_list_passwords",
            "hudu_get_password",
        ];
        public string CompaniesJson { get; init; } = HuduCompanyMapperTests.LiveCompactListFixture;
        public string ArticlesJson { get; init; } = HuduArticleMapperTests.LiveCompactListFixture;
        public string FoldersJson { get; init; } = """{"folders":[{"id":3,"name":"Networking","company_id":42}]}""";
        public string GetArticleJson { get; init; } = HuduArticleMapperTests.LiveCompactGetFixture;

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, "tools/list", null));
            var tools = Tools.Select(name => new { name }).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new { result = new { tools } }));
        }

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var inner = toolName switch
            {
                McpServerDefaults.HuduListCompaniesTool => CompaniesJson,
                McpServerDefaults.HuduListArticlesTool => ArticlesJson,
                McpServerDefaults.HuduListFoldersTool => FoldersJson,
                McpServerDefaults.HuduGetArticleTool => GetArticleJson,
                _ => throw new InvalidOperationException($"Unexpected Hudu tool call: {toolName}"),
            };
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, HuduMigrationService Import) Create(IMcpClient? mcp = null)
    {
        var user = new FakeCurrentUser { TenantId = Guid.NewGuid(), ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DocuEngAIneDbContext(options, user);
        return (db, user, new HuduMigrationService(db, user, mcp ?? new RecordingHuduMcp(), new NoopAudit()));
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
    {
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static async Task<McpServer> SeedCompactAsync(DocuEngAIneDbContext db, FakeCurrentUser user)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = McpServerDefaults.StackJackCompactName,
            Kind = McpServerKind.StackJackCompact,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    private static HuduImportPayload SamplePayload(int passwordCount = 0) => new(
        Companies:
        [
            new ExternalCompanyDto("42", "ExampleCo", "exampleco", PrimaryDomain: "example.com", Website: "https://example.com"),
        ],
        Articles:
        [
            new HuduArticleRecord("7", "VPN Setup", "<p>Use the company gateway.</p>", "vpn-setup", "42", FolderName: "Networking"),
        ],
        PasswordCount: passwordCount);

    [Fact]
    public void Hudu_Is_Not_An_IntegrationProvider_And_Is_Not_Compact_Backed()
    {
        Assert.DoesNotContain(Enum.GetNames<IntegrationProvider>(), n => n.Equals("Hudu", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("hudu", CompanyIdentity.HuduKey);
        Assert.NotEqual(CompanyIdentity.HuduKey, CompanyIdentity.ProviderKey(IntegrationProvider.CustomMcp));
        Assert.False(McpServerDefaults.IsCompactBacked(IntegrationProvider.Composio));
        Assert.True(McpServerDefaults.IsCompactBacked(IntegrationProvider.Blackpoint));
        Assert.Equal("hudu_", McpServerDefaults.HuduToolPrefix);
        Assert.StartsWith(McpServerDefaults.HuduToolPrefix, McpServerDefaults.HuduListCompaniesTool, StringComparison.Ordinal);
        Assert.StartsWith(McpServerDefaults.HuduToolPrefix, McpServerDefaults.HuduListArticlesTool, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payload_Import_Creates_Company_Folder_And_Document()
    {
        var (db, user, import) = Create();
        var server = await SeedCompactAsync(db, user);

        var result = await import.ImportAsync(server.Id, SamplePayload());
        Assert.NotNull(result);
        Assert.Equal(HuduMigrationService.PayloadSource, result.Source);
        Assert.Equal(1, result.CompaniesCreated);
        Assert.Equal(1, result.ArticlesCreated);
        Assert.Equal(0, result.PasswordsSkipped);
        Assert.Empty(result.ToolsUsed);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("ExampleCo", company.Name);
        Assert.Equal("42", CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)[CompanyIdentity.HuduKey]);

        var folder = await db.DocumentFolders.SingleAsync();
        Assert.Equal("Networking", folder.Name);
        Assert.Equal(company.Id, folder.CompanyId);

        var doc = await db.Documents.SingleAsync();
        Assert.Equal("VPN Setup", doc.Title);
        Assert.Equal(HuduMigrationService.ArticleSlug("7"), doc.Slug);
        Assert.Equal(HuduMigrationService.ArticleTag("7"), doc.Tags);
        Assert.Equal("<p>Use the company gateway.</p>", doc.Content);
        Assert.Equal(company.Id, doc.CompanyId);
        Assert.Equal(folder.Id, doc.FolderId);
        Assert.True(doc.IsPublished);
        Assert.Empty(await db.KeeperLinks.ToListAsync());
    }

    [Fact]
    public async Task Second_Import_Matches_Hudu_Ids_And_Does_Not_Duplicate()
    {
        var (db, user, import) = Create();
        var server = await SeedCompactAsync(db, user);

        var first = await import.ImportAsync(server.Id, SamplePayload());
        Assert.Equal(1, first!.CompaniesCreated);
        Assert.Equal(1, first.ArticlesCreated);

        var second = await import.ImportAsync(server.Id, SamplePayload());
        Assert.Equal(0, second!.CompaniesCreated);
        Assert.Equal(1, second.CompaniesUpdated);
        Assert.Equal(0, second.ArticlesCreated);
        Assert.Equal(1, second.ArticlesUpdated);

        Assert.Equal(1, await db.Companies.CountAsync());
        Assert.Equal(1, await db.Documents.CountAsync());
        Assert.Equal(1, await db.DocumentFolders.CountAsync());
        Assert.Single(CompanyIdentity.ReadExternalIds((await db.Companies.SingleAsync()).ExternalIdsJson));
    }

    [Fact]
    public async Task Import_Adopts_Existing_Company_By_Name_Instead_Of_Duplicating()
    {
        var (db, user, import) = Create();
        var server = await SeedCompactAsync(db, user);
        db.Companies.Add(new Company
        {
            TenantId = user.TenantId!.Value,
            Name = "ExampleCo",
            Slug = "exampleco",
            HaloClientId = "halo-100",
            ExternalIdsJson = CompanyIdentity.UpsertExternalId(null, CompanyIdentity.HaloKey, "halo-100"),
        });
        await db.SaveChangesAsync();

        var result = await import.ImportAsync(server.Id, SamplePayload());
        Assert.Equal(0, result!.CompaniesCreated);
        Assert.Equal(1, result.CompaniesUpdated);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("halo-100", company.HaloClientId);
        var ids = CompanyIdentity.ReadExternalIds(company.ExternalIdsJson);
        Assert.Equal("halo-100", ids["halo"]);
        Assert.Equal("42", ids[CompanyIdentity.HuduKey]);
        Assert.Equal(company.Id, (await db.Documents.SingleAsync()).CompanyId);
    }

    [Fact]
    public async Task Payload_Passwords_Are_Skipped_And_Never_Stored()
    {
        var (db, user, import) = Create();
        var server = await SeedCompactAsync(db, user);

        var result = await import.ImportAsync(server.Id, SamplePayload(passwordCount: 2));
        Assert.Equal(2, result!.PasswordsSkipped);
        Assert.Contains("Keeper", result.Message, StringComparison.Ordinal);
        Assert.Empty(await db.KeeperLinks.ToListAsync());
        Assert.DoesNotContain(await db.Documents.ToListAsync(), d =>
            (d.Content ?? "").Contains("password", StringComparison.OrdinalIgnoreCase)
            || (d.Title ?? "").Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Compact_Import_Calls_List_Tools_Never_Password_Tools_And_Fills_Missing_Content()
    {
        var mcp = new RecordingHuduMcp();
        var (db, user, import) = Create(mcp);
        var server = await SeedCompactAsync(db, user);

        var result = await import.ImportAsync(server.Id);
        Assert.NotNull(result);
        Assert.Equal(HuduMigrationService.CompactSource, result.Source);
        Assert.Equal(2, result.CompaniesCreated);
        Assert.Equal(2, result.ArticlesCreated);
        Assert.Contains(McpServerDefaults.HuduListCompaniesTool, result.ToolsUsed);
        Assert.Contains(McpServerDefaults.HuduListArticlesTool, result.ToolsUsed);
        Assert.Contains(McpServerDefaults.HuduListFoldersTool, result.ToolsUsed);
        Assert.Contains(McpServerDefaults.HuduGetArticleTool, result.ToolsUsed);

        Assert.DoesNotContain(mcp.Calls, c =>
            HuduMigrationService.PasswordToolNames.Contains(c.Tool, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Contains("password", StringComparison.OrdinalIgnoreCase));

        var vpn = await db.Documents.SingleAsync(d => d.Title == "VPN Setup");
        Assert.Equal("<p>Use the company gateway.</p>", vpn.Content);
        var folder = await db.DocumentFolders.SingleAsync(f => f.Id == vpn.FolderId);
        Assert.Equal("Networking", folder.Name);

        var draft = await db.Documents.SingleAsync(d => d.Title == "Draft Note");
        Assert.Equal("<p>Internal only.</p>", draft.Content);
        Assert.False(draft.IsPublished);
    }

    [Fact]
    public async Task Compact_Without_Hudu_Tools_And_Without_Payload_Is_BadRequest()
    {
        var mcp = new RecordingHuduMcp { Tools = ["halo_list_clients", "hudu_list_passwords"] };
        var (db, user, import) = Create(mcp);
        var server = await SeedCompactAsync(db, user);

        var result = await HuduMigrationEndpoints.ImportHuduAsync(
            new HuduImportRequest(server.Id), import, db, user);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Empty(await db.Companies.ToListAsync());
        Assert.DoesNotContain(mcp.Calls, c => c.Tool != "tools/list");
    }

    [Fact]
    public async Task Other_Tenant_McpServer_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingHuduMcp();

        Guid serverBId;
        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var server = await SeedCompactAsync(dbB, userB);
            serverBId = server.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var import = new HuduMigrationService(dbA, userA, mcp, new NoopAudit());
            var result = await HuduMigrationEndpoints.ImportHuduAsync(
                new HuduImportRequest(serverBId, [
                    new SyncCompanyDto("42", "PoisonCo"),
                ]), import, dbA, userA);

            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Documents.ForTenant(userA).ToListAsync());
        }
    }

    [Fact]
    public async Task Composio_Server_Is_404_Same_As_Missing()
    {
        var (db, user, import) = Create();
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "Composio",
            Kind = McpServerKind.Composio,
            EndpointUrl = McpServerDefaults.ComposioEndpoint,
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var result = await HuduMigrationEndpoints.ImportHuduAsync(
            new HuduImportRequest(server.Id), import, db, user);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Article_For_Unknown_Hudu_Company_Is_Skipped()
    {
        var (db, user, import) = Create();
        var server = await SeedCompactAsync(db, user);

        var result = await import.ImportAsync(server.Id, new HuduImportPayload(
            Companies: [new ExternalCompanyDto("42", "ExampleCo")],
            Articles: [new HuduArticleRecord("1", "Orphan", CompanyExternalId: "999")]));

        Assert.Equal(1, result!.CompaniesCreated);
        Assert.Equal(0, result.ArticlesCreated);
        Assert.Equal(1, result.ArticlesSkipped);
        Assert.Empty(await db.Documents.ToListAsync());
    }

    [Fact]
    public async Task Central_Kb_Article_Lands_In_Hudu_Folder_Without_Company()
    {
        var (db, user, import) = Create();
        var server = await SeedCompactAsync(db, user);

        var result = await import.ImportAsync(server.Id, new HuduImportPayload(
            Companies: [],
            Articles: [new HuduArticleRecord("15", "Global SOP", "<p>Shared.</p>")]));

        Assert.Equal(1, result!.ArticlesCreated);
        var doc = await db.Documents.SingleAsync();
        Assert.Null(doc.CompanyId);
        var folder = await db.DocumentFolders.SingleAsync();
        Assert.Equal(HuduMigrationService.DefaultFolderName, folder.Name);
        Assert.Null(folder.CompanyId);
        Assert.Equal(folder.Id, doc.FolderId);
    }

    [Fact]
    public async Task Endpoint_Payload_Import_Returns_Ok_And_Skips_Password_Json()
    {
        var (db, user, import) = Create();
        var server = await SeedCompactAsync(db, user);
        using var password = JsonDocument.Parse("""{"id":99,"name":"Admin","password":"should-never-be-stored"}""");

        var result = await HuduMigrationEndpoints.ImportHuduAsync(
            new HuduImportRequest(
                server.Id,
                [new SyncCompanyDto("42", "ExampleCo", Website: "https://example.com")],
                [new HuduArticleImportDto("7", "VPN Setup", "<p>Use the company gateway.</p>", CompanyExternalId: "42", FolderName: "Hudu")],
                [password.RootElement.Clone()]),
            import, db, user);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var body = Assert.IsType<HuduImportResult>(value.Value);
        Assert.Equal(1, body.PasswordsSkipped);
        Assert.Empty(await db.KeeperLinks.ToListAsync());
        Assert.Equal(HuduMigrationService.DefaultFolderName, (await db.DocumentFolders.SingleAsync()).Name);
    }
}

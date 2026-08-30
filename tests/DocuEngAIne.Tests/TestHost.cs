using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuEngAIne.Tests;

/// <summary>
/// In-process host for HTTP pipeline tests. Replaces SQL Server with EF InMemory and Entra JWT
/// with <see cref="TestAuthHandler"/> so the real middleware and endpoint groups run without a
/// live authority or database. <see cref="DocuEngAIne.Core.Interfaces.ICurrentUser"/> stays the
/// production implementation and reads the claims the test handler attaches.
/// </summary>
public sealed class TestHost : WebApplicationFactory<Program>
{
    public const string OwnerEmail = "owner@example.test";
    public const string ReaderEmail = "reader@example.test";
    public const string ContributorEmail = "contributor@example.test";

    private readonly string _databaseName = Guid.NewGuid().ToString();

    public TestHost()
    {
        // Must be set before Program.Main / CreateBuilder reads configuration.
        ApplyHostConfiguration();
    }

    public Guid TenantAId { get; } = Guid.NewGuid();
    public Guid TenantBId { get; } = Guid.NewGuid();
    public string OwnerObjectId { get; } = Guid.NewGuid().ToString();
    public string ReaderObjectId { get; } = Guid.NewGuid().ToString();
    public string ContributorObjectId { get; } = Guid.NewGuid().ToString();
    public Guid OtherTenantCompanyId { get; } = Guid.NewGuid();
    public Guid OtherTenantFolderId { get; } = Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        ApplyHostConfiguration();

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DocuEngAIne"] = "Server=pipeline-tests;Database=unused;",
                ["EntraId:Authority"] = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0",
                ["EntraId:Audience"] = "api://docuengaine-pipeline-tests",
                ["Azure:KeyVault:VaultUri"] = "",
                ["Llm:Provider"] = "Ollama",
                ["Llm:Model"] = "llama3.1",
                ["Llm:Ollama:BaseUrl"] = "http://127.0.0.1:11434",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            RemoveDbContextRegistrations(services);
            services.AddDbContext<DocuEngAIneDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { });

            // Never let pipeline tests reach a live LLM. The stub is a singleton so endpoint
            // tests can inspect the last call after the request scope is disposed.
            services.AddSingleton<StubLlmClient>();
            services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<StubLlmClient>());

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                options.DefaultScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        ApplyHostConfiguration();
        var host = base.CreateHost(builder);
        Seed(host.Services);
        return host;
    }

    private static void ApplyHostConfiguration()
    {
        // Host settings lose to appsettings.json's empty connection string. Environment
        // variables are applied after JSON in WebApplication.CreateBuilder, so they win
        // and satisfy AddDocuEngAIneInfrastructure before we swap the DbContext.
        Environment.SetEnvironmentVariable("ConnectionStrings__DocuEngAIne", "Server=pipeline-tests;Database=unused;");
        Environment.SetEnvironmentVariable("EntraId__Authority", "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0");
        Environment.SetEnvironmentVariable("EntraId__Audience", "api://docuengaine-pipeline-tests");
        Environment.SetEnvironmentVariable("Azure__KeyVault__VaultUri", "");
        Environment.SetEnvironmentVariable("TogetherApiKey", "");
        Environment.SetEnvironmentVariable("AnthropicApiKey", "");
    }

    public HttpClient CreateAnonymousClient() => CreateClient();

    public HttpClient CreateAuthenticatedClient(string objectId, Guid tenantId, string? appRole = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            TestAuthHandler.SchemeName,
            TestAuthHandler.EncodeTicket(objectId, tenantId, appRole));
        return client;
    }

    public HttpClient CreateOwnerClient() =>
        CreateAuthenticatedClient(OwnerObjectId, TenantAId, nameof(UserRole.Owner));

    public HttpClient CreateReaderClient() =>
        CreateAuthenticatedClient(ReaderObjectId, TenantAId);

    public HttpClient CreateContributorClient() =>
        CreateAuthenticatedClient(ContributorObjectId, TenantAId, nameof(UserRole.Contributor));

    private void Seed(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        db.Database.EnsureCreated();

        db.Tenants.AddRange(
            new Tenant { Id = TenantAId, Name = "Tenant A", Slug = "tenant-a" },
            new Tenant { Id = TenantBId, Name = "Tenant B", Slug = "tenant-b" });

        db.Users.AddRange(
            new User
            {
                TenantId = TenantAId,
                EntraObjectId = OwnerObjectId,
                Email = OwnerEmail,
                DisplayName = "Owner",
                Role = UserRole.Owner,
            },
            new User
            {
                TenantId = TenantAId,
                EntraObjectId = ReaderObjectId,
                Email = ReaderEmail,
                DisplayName = "Reader",
                Role = UserRole.Reader,
            },
            new User
            {
                TenantId = TenantAId,
                EntraObjectId = ContributorObjectId,
                Email = ContributorEmail,
                DisplayName = "Contributor",
                Role = UserRole.Contributor,
            });

        db.Companies.Add(new Company
        {
            Id = OtherTenantCompanyId,
            TenantId = TenantBId,
            Name = "PoisonCo",
            Slug = "poisonco",
        });

        db.DocumentFolders.Add(new DocumentFolder
        {
            Id = OtherTenantFolderId,
            TenantId = TenantBId,
            Name = "Poison-Folder",
        });

        db.SaveChanges();
    }

    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var remove = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(DocuEngAIneDbContext)
                || descriptor.ServiceType == typeof(DbContextOptions<DocuEngAIneDbContext>)
                || (descriptor.ServiceType.IsGenericType
                    && descriptor.ServiceType.GenericTypeArguments.Contains(typeof(DocuEngAIneDbContext))))
            .ToList();

        foreach (var descriptor in remove)
            services.Remove(descriptor);
    }
}

/// <summary>
/// Authenticates from an <c>Authorization: Test oid|tid|role</c> header. No network, no Entra.
/// Missing header is <see cref="AuthenticateResult.NoResult"/> so <c>RequireAuthorization</c> 401s.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    public static string EncodeTicket(string objectId, Guid tenantId, string? appRole = null)
        => $"{objectId}|{tenantId:D}|{appRole}";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
            return Task.FromResult(AuthenticateResult.NoResult());

        var raw = header.ToString();
        var prefix = SchemeName + " ";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var parts = raw[prefix.Length..].Split('|', 3);
        if (parts.Length < 2
            || string.IsNullOrWhiteSpace(parts[0])
            || !Guid.TryParse(parts[1], out _))
        {
            return Task.FromResult(AuthenticateResult.Fail("Malformed test ticket."));
        }

        var claims = new List<Claim>
        {
            new("oid", parts[0]),
            new("tid", parts[1]),
            new("preferred_username", $"{parts[0]}@example.test"),
            new("name", "Pipeline Test User"),
        };

        if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
            claims.Add(new Claim("roles", parts[2]));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

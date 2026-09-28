using System.Text.Json.Serialization;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Infrastructure;
using DocuEngAIne.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Configuration.AddAzureKeyVaultIfConfigured();

builder.Services.AddDocuEngAIneInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<DocuEngAIne.Infrastructure.Data.DocuEngAIneDbContext>(
        name: "sql",
        tags: ["ready"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme. Example: 'Bearer {token}'",
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer",
                },
            },
            Array.Empty<string>()
        },
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/api/health/live").AllowAnonymous();
app.MapHealthChecks("/api/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

app.MapTenantEndpoints();
app.MapCompanyEndpoints();
app.MapIntegrationEndpoints();
app.MapItGlueMigrationEndpoints();
app.MapHuduMigrationEndpoints();
app.MapAssetEndpoints();
app.MapExpirationEndpoints();
app.MapFlagEndpoints();
app.MapLinkEndpoints();
app.MapDocumentEndpoints();
app.MapSearchEndpoints();
app.MapFolderEndpoints();
app.MapKeeperLinkEndpoints();
app.MapRunbookEndpoints();
app.MapProfileEndpoints();
app.MapUserEndpoints();
app.MapResourceAccessEndpoints();
app.MapApiTokenEndpoints();
app.MapPortalEndpoints();
app.MapOutboundMcpEndpoints();
app.MapLlmEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

// Public so WebApplicationFactory<Program> in the test host can resolve the entry point.
// Top-level statements otherwise emit an internal Program type.
public partial class Program;

using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Tenant configuration: optional features from the registered catalog, what the app calls things,
/// and the header name and accent color. Everyone in the tenant reads it (the app renders from it);
/// only Admins and Owners change it, and every change is audited with what it moved from. The
/// configuration routes are never gated by a feature, so a feature that is off can be turned back on.
/// </summary>
public static class TenantConfigurationEndpoints
{
    public const string UnknownFeatureMessage = "Unknown feature.";

    public static IEndpointRouteBuilder MapTenantConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tenant/configuration", GetAsync).RequireAuthorization();

        var admin = app.MapGroup("/api/tenant").RequireAuthorization(AuthExtensions.AdminPolicy);
        admin.MapPut("/features/{key}", SetFeatureAsync);
        admin.MapPut("/terminology", SetTerminologyAsync);
        admin.MapPut("/branding", SetBrandingAsync);

        return app;
    }

    /// <summary>
    /// Gates every endpoint <paramref name="builder"/> covers on a registered tenant feature: while
    /// the caller's tenant has it off, the answer is 403 <c>{ error: "feature_disabled", feature }</c>.
    /// </summary>
    public static TBuilder RequireTenantFeature<TBuilder>(this TBuilder builder, string key)
        where TBuilder : IEndpointConventionBuilder
    {
        var feature = TenantFeatures.Find(key)
            ?? throw new ArgumentException($"'{key}' is not a registered tenant feature.", nameof(key));

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            if (services.GetRequiredService<ICurrentUser>().TenantId is Guid tenantId
                && !await services.GetRequiredService<TenantFeatureService>()
                    .IsEnabledAsync(tenantId, feature.Key, context.HttpContext.RequestAborted))
            {
                return FeatureDisabled(feature);
            }

            return await next(context);
        });
    }

    public static IResult FeatureDisabled(TenantFeatureDefinition feature)
        => Results.Json(
            new
            {
                error = TenantFeatures.DisabledError,
                feature = feature.Key,
                message = $"{feature.Name} is turned off for this tenant. An administrator can turn it back on in Settings.",
            },
            statusCode: StatusCodes.Status403Forbidden);

    public sealed record FeatureView(string Key, string Name, string Description, bool Enabled, bool EnabledByDefault);

    public sealed record TermView(string Key, string Describes, string Singular, string Plural, string DefaultSingular, string DefaultPlural);

    public sealed record BrandingView(string? DisplayName, string? AccentColor);

    public sealed record TenantConfigurationView(
        IReadOnlyList<FeatureView> Features,
        IReadOnlyList<TermView> Terminology,
        BrandingView Branding);

    public sealed record SetFeatureRequest(bool Enabled);

    public sealed record TermInput(string? Singular, string? Plural);

    /// <summary>Terms to change, by key. A null entry, or both forms blank, restores the default.</summary>
    public sealed record SetTerminologyRequest(Dictionary<string, TermInput?>? Terms);

    /// <summary>Both are replaced: null or blank clears a value back to the default.</summary>
    public sealed record SetBrandingRequest(string? DisplayName, string? AccentColor);

    public static async Task<IResult> GetAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        TenantFeatureService features,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();

        // A tenant that has not been onboarded yet simply has every default.
        var tenant = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.DisplayName, t.AccentColor, t.TerminologyJson })
            .FirstOrDefaultAsync(cancellationToken);
        var states = await features.GetStatesAsync(tenantId, cancellationToken);

        return Results.Ok(new TenantConfigurationView(
            MapFeatures(states),
            MapTerms(TenantAppearance.ParseTerms(tenant?.TerminologyJson)),
            new BrandingView(tenant?.DisplayName, tenant?.AccentColor)));
    }

    public static async Task<IResult> SetFeatureAsync(
        string key,
        [FromBody] SetFeatureRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        TenantFeatureService features,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();
        var feature = TenantFeatures.Find(key);
        if (feature is null)
            return Results.NotFound(new { error = UnknownFeatureMessage, key });
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return Results.NotFound(TenantEndpoints.TenantNotOnboardedMessage);

        var setting = await db.TenantFeatureSettings.ForTenant(user)
            .FirstOrDefaultAsync(s => s.Key == feature.Key, cancellationToken);
        var before = setting?.IsEnabled ?? feature.EnabledByDefault;
        if (setting is null)
        {
            setting = new TenantFeatureSetting { TenantId = tenantId, Key = feature.Key };
            db.TenantFeatureSettings.Add(setting);
        }

        setting.IsEnabled = request.Enabled;
        setting.UpdatedByObjectId = user.ObjectId;
        await db.SaveChangesAsync(cancellationToken);
        features.Invalidate(tenantId);

        if (before != request.Enabled && audit is not null)
        {
            await audit.LogAsync(
                new AuditEntry(
                    "Tenant.SetFeature",
                    nameof(Tenant),
                    tenantId,
                    $"{feature.Name} turned {(request.Enabled ? "on" : "off")}",
                    Category: AuditCategories.System,
                    TargetLabel: feature.Name,
                    ChangesJson: JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        [$"features.{feature.Key}"] = new { from = before, to = request.Enabled },
                    })),
                cancellationToken);
        }

        return Results.Ok(MapFeatures(await features.GetStatesAsync(tenantId, cancellationToken)));
    }

    public static async Task<IResult> SetTerminologyAsync(
        [FromBody] SetTerminologyRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return Results.NotFound(TenantEndpoints.TenantNotOnboardedMessage);

        var overrides = TenantAppearance.ParseTerms(tenant.TerminologyJson);
        var before = TenantAppearance.EffectiveTerms(overrides);
        foreach (var (key, input) in request.Terms ?? new Dictionary<string, TermInput?>())
        {
            var term = TenantTerms.Find(key);
            if (term is null)
                return Results.BadRequest(new { error = $"'{key}' is not a name that can be changed.", key });

            if (input is null || (string.IsNullOrWhiteSpace(input.Singular) && string.IsNullOrWhiteSpace(input.Plural)))
            {
                overrides.Remove(term.Key);
                continue;
            }

            if (!TenantAppearance.TryCleanLabel(input.Singular, TenantTerms.MaxLength, out var singular)
                || !TenantAppearance.TryCleanLabel(input.Plural, TenantTerms.MaxLength, out var plural))
            {
                return Results.BadRequest(new
                {
                    error = $"'{term.Singular}' needs both a singular and a plural name of 1–{TenantTerms.MaxLength} characters.",
                    key,
                });
            }

            if (singular == term.Singular && plural == term.Plural)
                overrides.Remove(term.Key);
            else
                overrides[term.Key] = new TenantAppearance.Term(singular, plural);
        }

        var after = TenantAppearance.EffectiveTerms(overrides);
        var changes = new Dictionary<string, object>();
        foreach (var (key, now) in after)
        {
            var was = before[key];
            if (was.Singular != now.Singular)
                changes[$"terminology.{key}.singular"] = new { from = was.Singular, to = now.Singular };
            if (was.Plural != now.Plural)
                changes[$"terminology.{key}.plural"] = new { from = was.Plural, to = now.Plural };
        }

        if (changes.Count > 0)
        {
            tenant.TerminologyJson = TenantAppearance.SerializeTerms(overrides);
            await db.SaveChangesAsync(cancellationToken);
            if (audit is not null)
            {
                await audit.LogAsync(
                    new AuditEntry(
                        "Tenant.SetTerminology",
                        nameof(Tenant),
                        tenantId,
                        "Changed what the app calls things",
                        Category: AuditCategories.System,
                        TargetLabel: tenant.Name,
                        ChangesJson: JsonSerializer.Serialize(changes)),
                    cancellationToken);
            }
        }

        return Results.Ok(MapTerms(overrides));
    }

    public static async Task<IResult> SetBrandingAsync(
        [FromBody] SetBrandingRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return Results.NotFound(TenantEndpoints.TenantNotOnboardedMessage);

        if (!TenantAppearance.TryCleanDisplayName(request.DisplayName, out var displayName, out var nameError))
            return Results.BadRequest(nameError);
        if (!TenantAppearance.TryNormalizeColor(request.AccentColor, out var accentColor, out var colorError))
            return Results.BadRequest(colorError);

        var changes = new Dictionary<string, object>();
        if (displayName != tenant.DisplayName)
            changes["branding.displayName"] = new { from = tenant.DisplayName, to = displayName };
        if (accentColor != tenant.AccentColor)
            changes["branding.accentColor"] = new { from = tenant.AccentColor, to = accentColor };

        if (changes.Count > 0)
        {
            tenant.DisplayName = displayName;
            tenant.AccentColor = accentColor;
            await db.SaveChangesAsync(cancellationToken);
            if (audit is not null)
            {
                await audit.LogAsync(
                    new AuditEntry(
                        "Tenant.SetBranding",
                        nameof(Tenant),
                        tenantId,
                        "Changed the app's name or accent color",
                        Category: AuditCategories.System,
                        TargetLabel: tenant.Name,
                        ChangesJson: JsonSerializer.Serialize(changes)),
                    cancellationToken);
            }
        }

        return Results.Ok(new BrandingView(tenant.DisplayName, tenant.AccentColor));
    }

    private static List<FeatureView> MapFeatures(IReadOnlyDictionary<string, bool> states)
        => TenantFeatures.All
            .Select(f => new FeatureView(
                f.Key,
                f.Name,
                f.Description,
                states.TryGetValue(f.Key, out var enabled) ? enabled : f.EnabledByDefault,
                f.EnabledByDefault))
            .ToList();

    private static List<TermView> MapTerms(IReadOnlyDictionary<string, TenantAppearance.Term> overrides)
    {
        var effective = TenantAppearance.EffectiveTerms(overrides);
        return TenantTerms.All
            .Select(t => new TermView(t.Key, t.Describes, effective[t.Key].Singular, effective[t.Key].Plural, t.Singular, t.Plural))
            .ToList();
    }
}

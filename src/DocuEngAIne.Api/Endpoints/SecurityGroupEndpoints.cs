using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Security groups: confine users to specific companies. A group without company grants restricts
/// nothing; a member of any group with grants sees only the granted companies, at the highest
/// level (View / Edit / Manage) any of their groups grants. Admins and Owners are never restricted,
/// so an administrator cannot lock themselves out here. Changes apply on the member's next request.
/// </summary>
public static class SecurityGroupEndpoints
{
    public const string NameRequiredMessage = "Name is required.";
    public const string DuplicateNameMessage = "A security group with that name already exists.";
    public const string InvalidLevelMessage = "Level must be View, Edit or Manage.";
    public const string UserNotFoundMessage = "User not found.";
    public const string CompanyNotFoundMessage = "Company not found.";

    public static IEndpointRouteBuilder MapSecurityGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/security-groups").RequireAuthorization(AuthExtensions.AdminPolicy);

        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapPut("/{id:guid}/members/{userId:guid}", AddMemberAsync);
        group.MapDelete("/{id:guid}/members/{userId:guid}", RemoveMemberAsync);
        group.MapPut("/{id:guid}/companies/{companyId:guid}", SetCompanyAsync);
        group.MapDelete("/{id:guid}/companies/{companyId:guid}", RemoveCompanyAsync);
        group.MapGet("/effective/{userId:guid}", EffectiveAsync);

        // The caller's own scope, so the SPA can say why a company is missing. Not admin-gated.
        app.MapGet("/api/me/company-access", MyAccessAsync).RequireAuthorization();

        return app;
    }

    public sealed record CreateGroupRequest(string? Name, string? Description = null, bool IncludeTenantWide = false);
    public sealed record UpdateGroupRequest(string? Name = null, string? Description = null, bool? IncludeTenantWide = null);
    public sealed record SetCompanyRequest(CompanyAccessLevel Level);

    public sealed record GroupSummary(
        Guid Id,
        string Name,
        string? Description,
        bool IncludeTenantWide,
        int MemberCount,
        int CompanyCount,
        DateTimeOffset CreatedAt);

    /// <param name="Bypasses">Admin or Owner: membership never restricts them.</param>
    public sealed record MemberView(Guid UserId, string Email, string? DisplayName, UserRole Role, bool IsActive, bool Bypasses);
    public sealed record CompanyGrantView(Guid CompanyId, string CompanyName, CompanyAccessLevel Level);
    public sealed record GroupDetail(GroupSummary Group, IReadOnlyList<MemberView> Members, IReadOnlyList<CompanyGrantView> Companies);
    public sealed record AccessView(bool Restricted, bool IncludesTenantWide, IReadOnlyList<CompanyGrantView> Companies);

    public static async Task<IResult> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var groups = await db.SecurityGroups.ForTenant(user).AsNoTracking()
            .OrderBy(g => g.Name)
            .Select(g => new GroupSummary(
                g.Id,
                g.Name,
                g.Description,
                g.IncludeTenantWide,
                g.Members.Count,
                g.CompanyGrants.Count,
                g.CreatedAt))
            .ToListAsync(cancellationToken);
        return Results.Ok(groups);
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var detail = await LoadDetailAsync(db, user, id, cancellationToken);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    public static async Task<IResult> CreateAsync(
        [FromBody] CreateGroupRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();

        var name = Clean(request.Name, 100);
        if (name is null)
            return Results.BadRequest(NameRequiredMessage);
        if (await db.SecurityGroups.ForTenant(user).AnyAsync(g => g.Name == name, cancellationToken))
            return Results.Conflict(new { error = DuplicateNameMessage });

        var group = new SecurityGroup
        {
            TenantId = tenantId,
            Name = name,
            Description = Clean(request.Description, 500),
            IncludeTenantWide = request.IncludeTenantWide,
            CreatedByObjectId = user.ObjectId,
        };
        db.SecurityGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "SecurityGroup.Create", nameof(SecurityGroup), group.Id, $"Created security group '{name}'", group.Name, cancellationToken);
        var detail = await LoadDetailAsync(db, user, group.Id, cancellationToken);
        return Results.Created($"/api/security-groups/{group.Id}", detail);
    }

    public static async Task<IResult> UpdateAsync(
        Guid id,
        [FromBody] UpdateGroupRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        var group = await db.SecurityGroups.ForTenant(user).FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
            return Results.NotFound();

        var changes = new Dictionary<string, object?>();
        if (request.Name is not null)
        {
            var name = Clean(request.Name, 100);
            if (name is null)
                return Results.BadRequest(NameRequiredMessage);
            if (name != group.Name)
            {
                if (await db.SecurityGroups.ForTenant(user).AnyAsync(g => g.Id != id && g.Name == name, cancellationToken))
                    return Results.Conflict(new { error = DuplicateNameMessage });
                changes["name"] = new { from = group.Name, to = name };
                group.Name = name;
            }
        }

        if (request.Description is not null)
        {
            var description = Clean(request.Description, 500);
            if (description != group.Description)
            {
                changes["description"] = new { from = group.Description, to = description };
                group.Description = description;
            }
        }

        if (request.IncludeTenantWide is bool includeTenantWide && includeTenantWide != group.IncludeTenantWide)
        {
            changes["includeTenantWide"] = new { from = group.IncludeTenantWide, to = includeTenantWide };
            group.IncludeTenantWide = includeTenantWide;
        }

        if (changes.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "SecurityGroup.Update", nameof(SecurityGroup), group.Id,
                $"Updated security group '{group.Name}'", group.Name, cancellationToken, JsonSerializer.Serialize(changes));
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    public static async Task<IResult> DeleteAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        var group = await db.SecurityGroups.ForTenant(user).FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
            return Results.NotFound();

        // Explicit rather than relying on the FK cascade, so every provider removes the same rows.
        db.SecurityGroupMembers.RemoveRange(
            await db.SecurityGroupMembers.ForTenant(user).Where(m => m.SecurityGroupId == id).ToListAsync(cancellationToken));
        db.SecurityGroupCompanyGrants.RemoveRange(
            await db.SecurityGroupCompanyGrants.ForTenant(user).Where(c => c.SecurityGroupId == id).ToListAsync(cancellationToken));
        db.SecurityGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "SecurityGroup.Delete", nameof(SecurityGroup), id, $"Deleted security group '{group.Name}'", group.Name, cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> AddMemberAsync(
        Guid id,
        Guid userId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();

        var group = await db.SecurityGroups.ForTenant(user).FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
            return Results.NotFound();
        var member = await db.Users.ForTenant(user).AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (member is null)
            return Results.NotFound(UserNotFoundMessage);

        if (!await db.SecurityGroupMembers.ForTenant(user).AnyAsync(m => m.SecurityGroupId == id && m.UserId == userId, cancellationToken))
        {
            db.SecurityGroupMembers.Add(new SecurityGroupMember { TenantId = tenantId, SecurityGroupId = id, UserId = userId });
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "SecurityGroup.AddMember", nameof(SecurityGroup), id,
                $"Added {member.Email} to '{group.Name}'", group.Name, cancellationToken);
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    public static async Task<IResult> RemoveMemberAsync(
        Guid id,
        Guid userId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        var group = await db.SecurityGroups.ForTenant(user).FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
            return Results.NotFound();

        var membership = await db.SecurityGroupMembers.ForTenant(user)
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.SecurityGroupId == id && m.UserId == userId, cancellationToken);
        if (membership is not null)
        {
            db.SecurityGroupMembers.Remove(membership);
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "SecurityGroup.RemoveMember", nameof(SecurityGroup), id,
                $"Removed {membership.User.Email} from '{group.Name}'", group.Name, cancellationToken);
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    public static async Task<IResult> SetCompanyAsync(
        Guid id,
        Guid companyId,
        [FromBody] SetCompanyRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();
        if (request.Level is not (CompanyAccessLevel.View or CompanyAccessLevel.Edit or CompanyAccessLevel.Manage))
            return Results.BadRequest(InvalidLevelMessage);

        var group = await db.SecurityGroups.ForTenant(user).FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
            return Results.NotFound();
        var company = await db.Companies.ForTenant(user).AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
            return Results.NotFound(CompanyNotFoundMessage);

        var grant = await db.SecurityGroupCompanyGrants.ForTenant(user)
            .FirstOrDefaultAsync(c => c.SecurityGroupId == id && c.CompanyId == companyId, cancellationToken);
        CompanyAccessLevel? previous = grant?.Level;
        if (grant is null)
        {
            db.SecurityGroupCompanyGrants.Add(new SecurityGroupCompanyGrant
            {
                TenantId = tenantId,
                SecurityGroupId = id,
                CompanyId = companyId,
                Level = request.Level,
            });
        }
        else
        {
            grant.Level = request.Level;
        }

        if (previous != request.Level)
        {
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "SecurityGroup.SetCompany", nameof(SecurityGroup), id,
                $"'{group.Name}' → {company.Name}: {request.Level}", group.Name, cancellationToken,
                JsonSerializer.Serialize(new { company = company.Name, level = new { from = previous?.ToString(), to = request.Level.ToString() } }));
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    public static async Task<IResult> RemoveCompanyAsync(
        Guid id,
        Guid companyId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        var group = await db.SecurityGroups.ForTenant(user).FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (group is null)
            return Results.NotFound();

        var grant = await db.SecurityGroupCompanyGrants.ForTenant(user)
            .Include(c => c.Company)
            .FirstOrDefaultAsync(c => c.SecurityGroupId == id && c.CompanyId == companyId, cancellationToken);
        if (grant is not null)
        {
            db.SecurityGroupCompanyGrants.Remove(grant);
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "SecurityGroup.RemoveCompany", nameof(SecurityGroup), id,
                $"'{group.Name}' no longer grants {grant.Company.Name}", group.Name, cancellationToken);
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    /// <summary>What a stored user can reach, for checking a group setup before relying on it.</summary>
    public static async Task<IResult> EffectiveAsync(
        Guid userId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CompanyScopeResolver resolver,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();
        if (!await db.Users.ForTenant(user).AnyAsync(u => u.Id == userId, cancellationToken))
            return Results.NotFound(UserNotFoundMessage);

        var scope = await resolver.ResolveForUserAsync(tenantId, userId, cancellationToken);
        return Results.Ok(await ToViewAsync(db, user, scope, cancellationToken));
    }

    public static async Task<IResult> MyAccessAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        ICompanyScopeAccessor? companyScope = null,
        CancellationToken cancellationToken = default)
    {
        var scope = companyScope?.Current ?? CompanyAccessScope.Unrestricted;
        return Results.Ok(await ToViewAsync(db, user, scope, cancellationToken));
    }

    private static async Task<AccessView> ToViewAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CompanyAccessScope scope,
        CancellationToken cancellationToken)
    {
        if (scope.IsUnrestricted)
            return new AccessView(false, true, []);

        var ids = scope.CompanyIds;
        var names = await db.Companies.ForTenant(user).AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var companies = scope.Levels
            .Where(l => names.ContainsKey(l.Key))
            .Select(l => new CompanyGrantView(l.Key, names[l.Key], l.Value))
            .OrderBy(c => c.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AccessView(true, scope.IncludesTenantWide, companies);
    }

    private static async Task<GroupDetail?> LoadDetailAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid id,
        CancellationToken cancellationToken)
    {
        var group = await db.SecurityGroups.ForTenant(user).AsNoTracking()
            .Where(g => g.Id == id)
            .Select(g => new GroupSummary(g.Id, g.Name, g.Description, g.IncludeTenantWide, g.Members.Count, g.CompanyGrants.Count, g.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null)
            return null;

        var members = await db.SecurityGroupMembers.ForTenant(user).AsNoTracking()
            .Where(m => m.SecurityGroupId == id)
            .OrderBy(m => m.User.Email)
            .Select(m => new MemberView(
                m.UserId,
                m.User.Email,
                m.User.DisplayName,
                m.User.Role,
                m.User.IsActive,
                m.User.Role >= UserRole.Admin))
            .ToListAsync(cancellationToken);
        var companies = await db.SecurityGroupCompanyGrants.ForTenant(user).AsNoTracking()
            .Where(c => c.SecurityGroupId == id)
            .OrderBy(c => c.Company.Name)
            .Select(c => new CompanyGrantView(c.CompanyId, c.Company.Name, c.Level))
            .ToListAsync(cancellationToken);
        return new GroupDetail(group, members, companies);
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static Task LogAsync(
        IAuditService? audit,
        string action,
        string entityType,
        Guid entityId,
        string details,
        string? label,
        CancellationToken cancellationToken,
        string? changesJson = null)
        => audit is null
            ? Task.CompletedTask
            : audit.LogAsync(
                new AuditEntry(action, entityType, entityId, details, AuditCategories.Access, label, changesJson),
                cancellationToken);
}

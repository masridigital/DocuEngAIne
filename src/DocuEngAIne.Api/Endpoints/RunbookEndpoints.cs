using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class RunbookEndpoints
{
    public static IEndpointRouteBuilder MapRunbookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/runbooks").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] string? search,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var query = db.Runbooks.ForTenant(user).AsNoTracking().Where(r => r.IsPublished);

            if (!string.IsNullOrWhiteSpace(search))
            {
                query = query.Where(r =>
                    (r.Title != null && r.Title.Contains(search)) ||
                    (r.Description != null && r.Description.Contains(search)) ||
                    (r.Tags != null && r.Tags.Contains(search)));
            }

            var runbooks = await query
                .OrderBy(r => r.Title)
                .Select(r => new { r.Id, r.Title, r.Slug, r.Description, r.Tags, r.CompanyId, r.UpdatedAt })
                .ToListAsync(cancellationToken);

            return Results.Ok(runbooks);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var runbook = await db.Runbooks
                .ForTenant(user)
                .AsNoTracking()
                .Include(r => r.Steps.OrderBy(s => s.Order))
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            return runbook is null ? Results.NotFound() : Results.Ok(new
            {
                runbook.Id,
                runbook.Title,
                runbook.Slug,
                runbook.Description,
                runbook.Tags,
                runbook.CompanyId,
                runbook.IsPublished,
                Steps = runbook.Steps.Select(s => new
                {
                    s.Id,
                    s.Order,
                    s.Title,
                    s.Details,
                    s.StepType,
                    s.IsRequired,
                    s.ExpectedOutput,
                }),
                runbook.UpdatedAt,
            });
        });

        group.MapPost("", async (
            [FromBody] CreateRunbookRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
                return badCompany;

            var runbook = new Runbook
            {
                TenantId = user.TenantId!.Value,
                Title = request.Title,
                Slug = request.Slug ?? request.Title.ToLowerInvariant().Replace(' ', '-'),
                Description = request.Description,
                Tags = request.Tags,
                IsPublished = request.IsPublished,
                CompanyId = request.CompanyId,
                Steps = request.Steps?.Select((s, i) => new RunbookStep
                {
                    Order = i + 1,
                    Title = s.Title,
                    Details = s.Details,
                    StepType = s.StepType,
                    IsRequired = s.IsRequired,
                    ExpectedOutput = s.ExpectedOutput,
                }).ToList() ?? [],
            };

            db.Runbooks.Add(runbook);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/runbooks/{runbook.Id}", new { runbook.Id, runbook.Title, runbook.Slug });
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateRunbookRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var runbook = await db.Runbooks
                .ForTenant(user)
                .Include(r => r.Steps)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (runbook is null)
                return Results.NotFound();

            if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
                return badCompany;
            if (request.CompanyId is Guid companyId)
                runbook.CompanyId = companyId;

            runbook.Title = request.Title ?? runbook.Title;
            runbook.Slug = request.Slug ?? runbook.Slug;
            runbook.Description = request.Description ?? runbook.Description;
            runbook.Tags = request.Tags ?? runbook.Tags;
            runbook.IsPublished = request.IsPublished ?? runbook.IsPublished;

            if (request.Steps is not null)
            {
                db.RunbookSteps.RemoveRange(runbook.Steps);
                runbook.Steps = request.Steps.Select((s, i) => new RunbookStep
                {
                    RunbookId = runbook.Id,
                    Order = i + 1,
                    Title = s.Title,
                    Details = s.Details,
                    StepType = s.StepType,
                    IsRequired = s.IsRequired,
                    ExpectedOutput = s.ExpectedOutput,
                }).ToList();
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var runbook = await db.Runbooks.ForTenant(user).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (runbook is null)
                return Results.NotFound();

            db.Runbooks.Remove(runbook);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateRunbookRequest(
    string Title,
    string? Slug,
    string? Description,
    string? Tags,
    bool IsPublished = true,
    List<RunbookStepRequest>? Steps = null,
    Guid? CompanyId = null);

public record UpdateRunbookRequest(
    string? Title,
    string? Slug,
    string? Description,
    string? Tags,
    bool? IsPublished,
    List<RunbookStepRequest>? Steps,
    Guid? CompanyId = null);

public record RunbookStepRequest(
    string Title,
    string? Details,
    string? StepType,
    bool IsRequired,
    string? ExpectedOutput);

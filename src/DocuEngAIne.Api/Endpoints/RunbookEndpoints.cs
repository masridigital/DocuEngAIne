using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class RunbookEndpoints
{
    public const string NotRunningMessage = "Run is not running.";

    public static IEndpointRouteBuilder MapRunbookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/runbooks").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] string? search,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var runbooks = await ListPublishedAsync(db, user, search, cancellationToken);
            return Results.Ok(runbooks);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await GetAsync(id, db, user, cancellationToken));

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

        group.MapGet("/{id:guid}/runs", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await ListRunsAsync(id, db, user, cancellationToken));

        group.MapPost("/{id:guid}/runs", async (
            Guid id,
            [FromBody] StartRunbookRunRequest? request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await StartRunAsync(id, request ?? new StartRunbookRunRequest(), db, user, cancellationToken));

        group.MapPost("/{id:guid}/runs/{runId:guid}/complete", async (
            Guid id,
            Guid runId,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await CompleteRunAsync(id, runId, db, user, cancellationToken));

        group.MapPost("/{id:guid}/runs/{runId:guid}/cancel", async (
            Guid id,
            Guid runId,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await CancelRunAsync(id, runId, db, user, cancellationToken));

        return app;
    }

    public static async Task<IReadOnlyList<RunbookListItem>> ListPublishedAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Runbooks.ForTenant(user).AsNoTracking().Where(r => r.IsPublished);

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(r =>
                (r.Title != null && r.Title.Contains(search)) ||
                (r.Description != null && r.Description.Contains(search)) ||
                (r.Tags != null && r.Tags.Contains(search)));
        }

        return await query
            .OrderBy(r => r.Title)
            .Select(r => new RunbookListItem(
                r.Id,
                r.Title,
                r.Slug,
                r.Description,
                r.Tags,
                r.CompanyId,
                r.UpdatedAt,
                r.Runs.Count()))
            .ToListAsync(cancellationToken);
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var runbook = await db.Runbooks
            .ForTenant(user)
            .AsNoTracking()
            .Include(r => r.Steps.OrderBy(s => s.Order))
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (runbook is null)
            return Results.NotFound();

        var runCount = await db.RunbookRuns.ForTenant(user).CountAsync(r => r.RunbookId == id, cancellationToken);
        return Results.Ok(new RunbookDetailItem(
            runbook.Id,
            runbook.Title,
            runbook.Slug,
            runbook.Description,
            runbook.Tags,
            runbook.CompanyId,
            runbook.IsPublished,
            runbook.Steps.Select(s => new RunbookStepItem(
                s.Id,
                s.Order,
                s.Title,
                s.Details,
                s.StepType,
                s.IsRequired,
                s.ExpectedOutput)).ToList(),
            runbook.UpdatedAt,
            runCount));
    }

    public static async Task<IResult> ListRunsAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var exists = await db.Runbooks.ForTenant(user).AsNoTracking().AnyAsync(r => r.Id == id, cancellationToken);
        if (!exists)
            return Results.NotFound();

        var runs = await db.RunbookRuns.ForTenant(user).AsNoTracking()
            .Where(r => r.RunbookId == id)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new RunbookRunItem(
                r.Id,
                r.RunbookId,
                r.CompanyId,
                r.Status,
                r.StartedAt,
                r.FinishedAt,
                r.StartedByObjectId))
            .ToListAsync(cancellationToken);

        return Results.Ok(runs);
    }

    public static async Task<IResult> StartRunAsync(
        Guid id,
        StartRunbookRunRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var runbook = await db.Runbooks.ForTenant(user).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (runbook is null)
            return Results.NotFound();

        if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
            return badCompany;

        var run = new RunbookRun
        {
            TenantId = user.TenantId.Value,
            RunbookId = runbook.Id,
            CompanyId = request.CompanyId ?? runbook.CompanyId,
            Status = RunbookRunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            StartedByObjectId = user.ObjectId,
        };
        db.RunbookRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/runbooks/{runbook.Id}/runs/{run.Id}", MapRun(run));
    }

    public static Task<IResult> CompleteRunAsync(
        Guid id,
        Guid runId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default) =>
        FinishRunAsync(id, runId, RunbookRunStatus.Completed, db, user, cancellationToken);

    public static Task<IResult> CancelRunAsync(
        Guid id,
        Guid runId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default) =>
        FinishRunAsync(id, runId, RunbookRunStatus.Cancelled, db, user, cancellationToken);

    private static async Task<IResult> FinishRunAsync(
        Guid runbookId,
        Guid runId,
        RunbookRunStatus status,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var run = await db.RunbookRuns.ForTenant(user)
            .FirstOrDefaultAsync(r => r.Id == runId && r.RunbookId == runbookId, cancellationToken);
        if (run is null)
            return Results.NotFound();
        if (run.Status != RunbookRunStatus.Running)
            return Results.BadRequest(NotRunningMessage);

        run.Status = status;
        run.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(MapRun(run));
    }

    private static RunbookRunItem MapRun(RunbookRun run) => new(
        run.Id,
        run.RunbookId,
        run.CompanyId,
        run.Status,
        run.StartedAt,
        run.FinishedAt,
        run.StartedByObjectId);
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

public record StartRunbookRunRequest(Guid? CompanyId = null);

public record RunbookListItem(
    Guid Id,
    string Title,
    string? Slug,
    string? Description,
    string? Tags,
    Guid? CompanyId,
    DateTimeOffset UpdatedAt,
    int RunCount);

public record RunbookDetailItem(
    Guid Id,
    string Title,
    string? Slug,
    string? Description,
    string? Tags,
    Guid? CompanyId,
    bool IsPublished,
    IReadOnlyList<RunbookStepItem> Steps,
    DateTimeOffset UpdatedAt,
    int RunCount);

public record RunbookStepItem(
    Guid Id,
    int Order,
    string Title,
    string? Details,
    string? StepType,
    bool IsRequired,
    string? ExpectedOutput);

public record RunbookRunItem(
    Guid Id,
    Guid RunbookId,
    Guid? CompanyId,
    RunbookRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? StartedByObjectId);

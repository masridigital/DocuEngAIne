using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Identity;

public class AuditService : IAuditService
{
    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditService(DocuEngAIneDbContext db, ICurrentUser currentUser, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _currentUser = currentUser;
        _httpContextAccessor = httpContextAccessor;
    }

    public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
        => LogAsync(new AuditEntry(action, entityType, entityId, details), cancellationToken);

    public async Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Guid? userId = null;
        string? actorName = null;
        if (_currentUser.IsAuthenticated && _currentUser.TenantId.HasValue && !string.IsNullOrEmpty(_currentUser.ObjectId))
        {
            var user = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.TenantId == _currentUser.TenantId.Value && u.EntraObjectId == _currentUser.ObjectId, cancellationToken);
            userId = user?.Id;
            actorName = user?.DisplayName ?? user?.Email;
        }

        var http = _httpContextAccessor.HttpContext;
        var log = new AuditLog
        {
            TenantId = _currentUser.TenantId,
            UserId = userId,
            ActorObjectId = _currentUser.ObjectId,
            ActorName = actorName,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            Details = entry.Details,
            Category = entry.Category,
            TargetLabel = entry.TargetLabel,
            ChangesJson = entry.ChangesJson,
            IpAddress = http?.Connection?.RemoteIpAddress?.ToString(),
            RequestMethod = http?.Request?.Method,
            RequestPath = Truncate(http?.Request?.Path.Value, 512),
            UserAgent = Truncate(http?.Request?.Headers.UserAgent.ToString(), 512),
        };

        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}

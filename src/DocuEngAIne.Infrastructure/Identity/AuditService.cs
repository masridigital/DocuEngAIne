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

    public async Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
    {
        Guid? userId = null;
        if (_currentUser.IsAuthenticated && _currentUser.TenantId.HasValue && !string.IsNullOrEmpty(_currentUser.ObjectId))
        {
            var user = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.TenantId == _currentUser.TenantId.Value && u.EntraObjectId == _currentUser.ObjectId, cancellationToken);
            userId = user?.Id;
        }

        var log = new AuditLog
        {
            TenantId = _currentUser.TenantId,
            UserId = userId,
            ActorObjectId = _currentUser.ObjectId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details,
            IpAddress = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString(),
        };

        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
    }
}

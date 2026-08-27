using DocuEngAIne.Core.Interfaces;
using System.Linq.Expressions;

namespace DocuEngAIne.Infrastructure.Data;

public static class QueryableExtensions
{
    public static IQueryable<T> ForTenant<T>(this IQueryable<T> source, ICurrentUser currentUser)
        where T : class, ITenantScoped
    {
        if (currentUser.TenantId is null)
            throw new InvalidOperationException("Tenant is required for this operation.");

        return source.Where(e => e.TenantId == currentUser.TenantId.Value);
    }
}

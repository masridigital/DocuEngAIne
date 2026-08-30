using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Identity;

/// <summary>
/// Ambient <see cref="ICurrentUser"/> for scopes that are not an Entra JWT on an HTTP request:
/// the outbound MCP server today, the sync scheduler later. <see cref="CurrentUser"/> reads this
/// first so <c>ForTenant</c>, audit, and <c>SaveChanges</c> tenant-stamping keep working without
/// a browser session.
/// </summary>
public static class CurrentUserScope
{
    private static readonly AsyncLocal<ICurrentUser?> Ambient = new();

    public static ICurrentUser? Current => Ambient.Value;

    public static IDisposable Use(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var previous = Ambient.Value;
        Ambient.Value = user;
        return new Restorer(previous);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly ICurrentUser? _previous;
        private bool _disposed;

        public Restorer(ICurrentUser? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
                return;
            Ambient.Value = _previous;
            _disposed = true;
        }
    }
}

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace DocuEngAIne.Infrastructure.Security;

/// <summary>CIDR parsing and matching for the tenant IP allowlist. IPv4 and IPv6.</summary>
public static class IpAllowlist
{
    /// <summary>
    /// Parses <c>addr</c> or <c>addr/prefix</c>, masks host bits to the network address, and
    /// returns the canonical form, so <c>10.0.0.5/24</c> and <c>10.0.0.0/24</c> are one entry.
    /// IPv4-mapped IPv6 input is stored as IPv4.
    /// </summary>
    public static bool TryNormalize(string? input, out IPNetwork network, out string normalized)
    {
        network = default;
        normalized = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var text = input.Trim();
        var slash = text.IndexOf('/');
        var addressPart = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressPart, out var parsed))
            return false;

        // IPAddress.TryParse also takes inet_aton shorthand ("10" is 0.0.0.10, "010.0.0.1" is
        // octal). An allowlist must mean exactly what the admin typed, so IPv4 must be dotted-quad.
        if (parsed.AddressFamily == AddressFamily.InterNetwork && parsed.ToString() != addressPart)
            return false;

        var address = Normalize(parsed)!;
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxPrefix;
        if (slash >= 0
            && (!int.TryParse(text[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
                || prefix > maxPrefix))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= keep == 0 ? (byte)0 : (byte)(0xFF << (8 - keep));
        }

        network = new IPNetwork(new IPAddress(bytes), prefix);
        normalized = network.ToString();
        return true;
    }

    /// <summary>Dual-stack sockets report IPv4 clients as <c>::ffff:a.b.c.d</c>; compare them as IPv4.</summary>
    public static IPAddress? Normalize(IPAddress? address)
        => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>An unknown client address is never allowed: enforcement fails closed.</summary>
    public static bool Contains(IEnumerable<IPNetwork> networks, IPAddress? client)
    {
        var address = Normalize(client);
        return address is not null && networks.Any(n => n.Contains(address));
    }
}

/// <summary>
/// Per-tenant allowlist policy with a short cache: enforcement runs on every API request, and a
/// change takes effect within <see cref="CacheTtl"/> on every instance (immediately on the one
/// that made it). <see cref="DisableKey"/> is the break-glass: an app setting only someone with
/// Azure access can flip, for the day an administrator's own IP changes under them.
/// </summary>
public sealed class IpAllowlistService
{
    public const string DisableKey = "Security:DisableIpAllowlist";
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly DocuEngAIneDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;

    public IpAllowlistService(DocuEngAIneDbContext db, IMemoryCache cache, IConfiguration configuration)
    {
        _db = db;
        _cache = cache;
        _configuration = configuration;
    }

    public sealed record Policy(bool Enabled, IReadOnlyList<IPNetwork> Networks);

    public bool BreakGlass => _configuration.GetValue<bool>(DisableKey);

    public async Task<bool> IsAllowedAsync(Guid tenantId, IPAddress? client, CancellationToken cancellationToken = default)
    {
        if (BreakGlass)
            return true;

        var policy = await GetPolicyAsync(tenantId, cancellationToken);
        return !policy.Enabled || IpAllowlist.Contains(policy.Networks, client);
    }

    public async Task<Policy> GetPolicyAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(tenantId);
        if (_cache.TryGetValue(key, out Policy? cached) && cached is not null)
            return cached;

        var enabled = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.IpAllowlistEnabled)
            .FirstOrDefaultAsync(cancellationToken);

        var networks = new List<IPNetwork>();
        if (enabled)
        {
            var cidrs = await _db.IpAllowlistEntries.AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.IsActive)
                .Select(e => e.Cidr)
                .ToListAsync(cancellationToken);
            foreach (var cidr in cidrs)
            {
                if (IpAllowlist.TryNormalize(cidr, out var network, out _))
                    networks.Add(network);
            }
        }

        var policy = new Policy(enabled, networks);
        _cache.Set(key, policy, CacheTtl);
        return policy;
    }

    public void Invalidate(Guid tenantId) => _cache.Remove(CacheKey(tenantId));

    private static string CacheKey(Guid tenantId) => $"ip-allowlist:{tenantId:N}";
}

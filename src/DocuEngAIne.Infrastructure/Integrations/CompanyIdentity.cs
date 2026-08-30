using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Converges companies pulled from different providers onto one local <see cref="Company"/>.
/// Halo, NinjaOne, CIPP, Meraki, UniFi, Action1, Autotask and Blackpoint each own their own <see cref="IntegrationMapping"/> rows,
/// so without a match step the same client is created once per connection. Provider identity is
/// recorded in the typed Halo/Ninja columns where they exist and in
/// <see cref="Company.ExternalIdsJson"/> for every provider, which is also what later runs match on.
/// IT Glue is migrate-only: one-shot import stamps <see cref="ItGlueKey"/> and is not an
/// <see cref="IntegrationProvider"/>.
/// </summary>
public static class CompanyIdentity
{
    public const string HaloKey = "halo";
    public const string NinjaKey = "ninja";

    /// <summary>
    /// Stable <see cref="Company.ExternalIdsJson"/> key for the one-shot IT Glue import.
    /// Not an <see cref="IntegrationProvider"/> — IT Glue is not a live company-sync system of record.
    /// </summary>
    public const string ItGlueKey = "itglue";

    /// <summary>Stable key used inside <see cref="Company.ExternalIdsJson"/>. Never renamed once shipped.</summary>
    public static string ProviderKey(IntegrationProvider provider) => provider switch
    {
        IntegrationProvider.Halo => HaloKey,
        IntegrationProvider.NinjaOne => NinjaKey,
        IntegrationProvider.Cipp => "cipp",
        IntegrationProvider.Meraki => "meraki",
        IntegrationProvider.UniFi => "unifi",
        IntegrationProvider.Action1 => "action1",
        IntegrationProvider.Autotask => "autotask",
        IntegrationProvider.Blackpoint => "blackpoint",
        IntegrationProvider.Composio => "composio",
        _ => "custom",
    };

    /// <summary>
    /// Case- and punctuation-insensitive name key. "ExampleCo, Inc." and "Example Co Inc" both
    /// collapse to "examplecoinc". Deliberately does not strip legal suffixes — dropping "LLC"
    /// would merge genuinely distinct clients.
    /// </summary>
    public static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var chars = name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return chars.Length == 0 ? null : new string(chars);
    }

    /// <summary>Reduces a domain, URL or email to a bare host: "https://WWW.Example.com/x" becomes "example.com".</summary>
    public static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        var value = domain.Trim().ToLowerInvariant();

        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            value = value[(scheme + 3)..];

        // Trim the path before the userinfo strip, or "https://example.com/@acme" would reduce
        // to "acme" instead of "example.com".
        var slash = value.IndexOf('/');
        if (slash >= 0)
            value = value[..slash];

        var at = value.IndexOf('@');
        if (at >= 0)
            value = value[(at + 1)..];

        var colon = value.IndexOf(':');
        if (colon >= 0)
            value = value[..colon];

        if (value.StartsWith("www.", StringComparison.Ordinal))
            value = value[4..];

        value = value.Trim('.');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Reads <see cref="Company.ExternalIdsJson"/>. Unparsable metadata is treated as absent, never as a sync failure.</summary>
    public static Dictionary<string, string> ReadExternalIds(string? externalIdsJson)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(externalIdsJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(externalIdsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return result;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(value))
                    result[prop.Name] = value;
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>Returns <see cref="Company.ExternalIdsJson"/> with <paramref name="providerKey"/> set to <paramref name="externalId"/>.</summary>
    public static string UpsertExternalId(string? externalIdsJson, string providerKey, string externalId)
    {
        var ids = ReadExternalIds(externalIdsJson);
        ids[providerKey] = externalId;
        return JsonSerializer.Serialize(ids);
    }
}

/// <summary>How an incoming record was matched to an existing company. Recorded on the mapping for auditability.</summary>
public sealed record CompanyMatch(Company Company, string Reason);

/// <summary>
/// In-memory match index over one tenant's companies, built once per sync run.
/// A key that would resolve to two different companies is treated as ambiguous and stops matching
/// on that key — duplicating a company is recoverable, merging two real clients is not.
/// </summary>
public sealed class CompanyMatchIndex
{
    public const string MatchedByProviderId = "provider-id";
    public const string MatchedByDomain = "primary-domain";
    public const string MatchedByName = "name";

    private readonly Dictionary<string, Company?> _byProviderId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Company?> _byDomain = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Company?> _byName = new(StringComparer.Ordinal);

    public CompanyMatchIndex(IEnumerable<Company> companies)
    {
        foreach (var company in companies)
            Add(company);
    }

    public void Add(Company company)
    {
        Index(_byProviderId, ProviderIdKey(CompanyIdentity.HaloKey, company.HaloClientId), company);
        Index(_byProviderId, ProviderIdKey(CompanyIdentity.NinjaKey, company.NinjaOrganizationId), company);

        foreach (var pair in CompanyIdentity.ReadExternalIds(company.ExternalIdsJson))
            Index(_byProviderId, ProviderIdKey(pair.Key, pair.Value), company);

        Index(_byDomain, CompanyIdentity.NormalizeDomain(company.PrimaryDomain), company);
        Index(_byName, CompanyIdentity.NormalizeName(company.Name), company);
    }

    /// <summary>Provider id first (authoritative), then primary domain, then exact normalized name.</summary>
    public CompanyMatch? Find(string providerKey, ExternalCompanyDto dto)
    {
        if (TryGet(_byProviderId, ProviderIdKey(providerKey, dto.ExternalId), out var byProviderId))
            return new CompanyMatch(byProviderId, MatchedByProviderId);

        if (TryGet(_byDomain, CompanyIdentity.NormalizeDomain(dto.PrimaryDomain), out var byDomain))
            return new CompanyMatch(byDomain, MatchedByDomain);

        if (TryGet(_byName, CompanyIdentity.NormalizeName(dto.Name), out var byName))
            return new CompanyMatch(byName, MatchedByName);

        return null;
    }

    private static string? ProviderIdKey(string providerKey, string? externalId)
        => string.IsNullOrWhiteSpace(externalId) ? null : $"{providerKey}:{externalId}";

    private static void Index(Dictionary<string, Company?> index, string? key, Company company)
    {
        if (key is null)
            return;

        if (index.TryGetValue(key, out var existing))
        {
            if (existing is not null && existing.Id != company.Id)
                index[key] = null;
            return;
        }

        index[key] = company;
    }

    private static bool TryGet(Dictionary<string, Company?> index, string? key, out Company company)
    {
        company = null!;
        if (key is null || !index.TryGetValue(key, out var found) || found is null)
            return false;

        company = found;
        return true;
    }
}

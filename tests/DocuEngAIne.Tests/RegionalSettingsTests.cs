using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DocuEngAIne.Tests;

/// <summary>
/// A tenant's time zone and date / time formats: IANA zones and listed formats only, defaults stored
/// as nothing, every change audited, and expiration days counted in the tenant's zone.
/// </summary>
public class RegionalSettingsTests
{
    private sealed class RecordingAudit : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(action, entityType, entityId, details));
            return Task.CompletedTask;
        }

        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static async Task<(DocuEngAIneDbContext Db, FakeCurrentUser User, Guid TenantId)> SeedAsync(string? timeZoneId = null)
    {
        var tenantId = Guid.NewGuid();
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = "admin-oid", Email = "admin@example.com", Role = UserRole.Admin };
        var db = new DocuEngAIneDbContext(
            new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, user);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "MSP", Slug = $"msp-{tenantId:N}", TimeZoneId = timeZoneId });
        await db.SaveChangesAsync();
        return (db, user, tenantId);
    }

    private static int StatusOf(IResult? result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    private static T ValueOf<T>(IResult result) => Assert.IsAssignableFrom<T>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static async Task<TenantConfigurationEndpoints.RegionalView> ReadAsync(DocuEngAIneDbContext db, FakeCurrentUser user)
    {
        var features = new TenantFeatureService(db, new MemoryCache(new MemoryCacheOptions()));
        var result = await TenantConfigurationEndpoints.GetAsync(db, user, features);
        return ValueOf<TenantConfigurationEndpoints.TenantConfigurationView>(result).Regional;
    }

    private static Task<IResult> SetAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, string? timeZone, string? dateFormat, string? timeFormat, IAuditService? audit = null)
        => TenantConfigurationEndpoints.SetRegionalAsync(
            new TenantConfigurationEndpoints.SetRegionalRequest(timeZone, dateFormat, timeFormat), db, user, audit);

    [Fact]
    public async Task A_New_Tenant_Is_On_Utc_With_The_Standard_Formats()
    {
        var (db, user, _) = await SeedAsync();
        await using (db)
        {
            var regional = await ReadAsync(db, user);
            Assert.Equal("UTC", regional.TimeZone);
            Assert.Equal("yyyy-MM-dd", regional.DateFormat);
            Assert.Equal("24h", regional.TimeFormat);
            Assert.Equal(TenantRegional.DateFormats.Select(f => f.Key), regional.DateFormats.Select(f => f.Key));
            Assert.Equal(new[] { "24h", "12h" }, regional.TimeFormats.Select(f => f.Key).ToArray());
        }
    }

    [Fact]
    public async Task An_Admin_Sets_A_Zone_And_Formats_And_The_Change_Is_Audited()
    {
        var (db, user, tenantId) = await SeedAsync();
        await using (db)
        {
            var audit = new RecordingAudit();
            var result = await SetAsync(db, user, " Europe/London ", "dd/MM/yyyy", "12h", audit);

            Assert.Equal(200, StatusOf(result));
            var view = ValueOf<TenantConfigurationEndpoints.RegionalView>(result);
            Assert.Equal("Europe/London", view.TimeZone);
            Assert.Equal("dd/MM/yyyy", view.DateFormat);
            Assert.Equal("12h", view.TimeFormat);

            db.ChangeTracker.Clear();
            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
            Assert.Equal("Europe/London", tenant.TimeZoneId);
            Assert.Equal("dd/MM/yyyy", tenant.DateFormat);
            Assert.Equal("12h", tenant.TimeFormat);
            Assert.Equal("Europe/London", (await ReadAsync(db, user)).TimeZone);

            var entry = Assert.Single(audit.Entries);
            Assert.Equal("Tenant.SetRegional", entry.Action);
            Assert.Equal(AuditCategories.System, entry.Category);
            using var changes = JsonDocument.Parse(entry.ChangesJson!);
            Assert.Equal("UTC", changes.RootElement.GetProperty("regional.timeZone").GetProperty("from").GetString());
            Assert.Equal("Europe/London", changes.RootElement.GetProperty("regional.timeZone").GetProperty("to").GetString());
            Assert.Equal("12h", changes.RootElement.GetProperty("regional.timeFormat").GetProperty("to").GetString());

            // Saving the same settings again changes nothing and records nothing.
            await SetAsync(db, user, "Europe/London", "dd/MM/yyyy", "12h", audit);
            Assert.Single(audit.Entries);
        }
    }

    [Fact]
    public async Task Defaults_And_Blanks_Are_Stored_As_Nothing()
    {
        var (db, user, tenantId) = await SeedAsync("America/New_York");
        await using (db)
        {
            var audit = new RecordingAudit();
            Assert.Equal(200, StatusOf(await SetAsync(db, user, "UTC", "yyyy-MM-dd", "24h", audit)));

            db.ChangeTracker.Clear();
            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
            Assert.Null(tenant.TimeZoneId);
            Assert.Null(tenant.DateFormat);
            Assert.Null(tenant.TimeFormat);
            Assert.Single(audit.Entries);

            Assert.Equal(200, StatusOf(await SetAsync(db, user, "Asia/Tokyo", "d MMM yyyy", "12h", audit)));
            Assert.Equal(200, StatusOf(await SetAsync(db, user, null, " ", "", audit)));
            db.ChangeTracker.Clear();
            tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
            Assert.Null(tenant.TimeZoneId);
            Assert.Null(tenant.DateFormat);
            Assert.Null(tenant.TimeFormat);
            Assert.Equal("UTC", (await ReadAsync(db, user)).TimeZone);
        }
    }

    [Theory]
    [InlineData("Mars/Olympus", null, null)]
    [InlineData("Eastern Standard Time", null, null)]
    [InlineData(null, "yyyy/MM/dd", null)]
    [InlineData(null, null, "24")]
    public async Task Unknown_Zones_And_Formats_Are_Refused_And_Nothing_Is_Stored(string? timeZone, string? dateFormat, string? timeFormat)
    {
        var (db, user, tenantId) = await SeedAsync("Europe/London");
        await using (db)
        {
            var audit = new RecordingAudit();
            Assert.Equal(400, StatusOf(await SetAsync(db, user, timeZone, dateFormat, timeFormat, audit)));

            db.ChangeTracker.Clear();
            Assert.Equal("Europe/London", (await db.Tenants.SingleAsync(t => t.Id == tenantId)).TimeZoneId);
            Assert.Empty(audit.Entries);
        }
    }

    [Fact]
    public async Task A_Stored_Zone_This_Host_Does_Not_Know_Reads_As_Utc()
    {
        var (db, user, _) = await SeedAsync("Mars/Olympus");
        await using (db)
        {
            Assert.Equal("UTC", (await ReadAsync(db, user)).TimeZone);
            Assert.Same(TimeZoneInfo.Utc, TenantClock.ZoneFor("Mars/Olympus"));
        }
    }

    [Fact]
    public void The_Clock_Knows_Iana_Zones_Only_And_Puts_Instants_On_The_Local_Day()
    {
        Assert.True(TenantClock.TryFindZone("America/New_York", out var newYork));
        Assert.True(TenantClock.TryFindZone("UTC", out var utc));
        Assert.Same(TimeZoneInfo.Utc, utc);
        Assert.False(TenantClock.TryFindZone("Eastern Standard Time", out _));
        Assert.False(TenantClock.TryFindZone(null, out _));
        Assert.False(TenantClock.TryFindZone("  ", out _));

        var lateEvening = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 9, 30), TenantClock.DayOf(lateEvening, newYork));
        Assert.Equal(new DateOnly(2026, 10, 1), TenantClock.DayOf(lateEvening, TimeZoneInfo.Utc));
        Assert.Equal(new DateOnly(2026, 9, 30), TenantClock.Today(newYork, lateEvening));
    }

    /// <summary>
    /// At 02:00 UTC on 1 October it is still 30 September in New York. Each kind of expiration is
    /// counted from the tenant's today, and a calendar day is never moved by the zone.
    /// </summary>
    [Theory]
    [InlineData(null, 0, 0, 0, 0)]
    [InlineData("America/New_York", 1, 1, 1, 1)]
    public async Task Expiration_Days_Are_Counted_In_The_Tenant_Zone(
        string? timeZoneId, int dateFieldDays, int dateTimeFieldDays, int shortcutDays, int offsetShortcutDays)
    {
        var (db, user, tenantId) = await SeedAsync(timeZoneId);
        await using (db)
        {
            var type = new AssetType
            {
                TenantId = tenantId,
                Name = "Licenses",
                Fields =
                [
                    new FieldDefinition { Name = "Renewal", FieldType = "Date", IsExpiration = true, SortOrder = 0 },
                    new FieldDefinition { Name = "Cutover", FieldType = "DateTime", IsExpiration = true, SortOrder = 1 },
                ],
            };
            db.AssetTypes.Add(type);
            await db.SaveChangesAsync();

            var withFields = new Asset { TenantId = tenantId, Name = "M365", AssetTypeId = type.Id };
            withFields.CustomFieldValues.Add(new CustomFieldValue { FieldDefinitionId = type.Fields.Single(f => f.Name == "Renewal").Id, Value = "2026-10-01" });
            // 01:00 on 1 October in New York; still 1 October in UTC.
            withFields.CustomFieldValues.Add(new CustomFieldValue { FieldDefinitionId = type.Fields.Single(f => f.Name == "Cutover").Id, Value = "2026-10-01T05:00:00.0000000Z" });
            // A date shortcut: 1 October as written, whatever the offset it was written with.
            var shortcut = new Asset { TenantId = tenantId, Name = "Firewall", AssetTypeId = type.Id, ExpiresAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero) };
            var offsetShortcut = new Asset { TenantId = tenantId, Name = "Switch", AssetTypeId = type.Id, ExpiresAt = new DateTimeOffset(2026, 10, 1, 23, 0, 0, TimeSpan.FromHours(-5)) };
            db.Assets.AddRange(withFields, shortcut, offsetShortcut);
            await db.SaveChangesAsync();

            var now = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
            var items = await ExpirationEndpoints.QueryAsync(db, user, showExpired: true, utcNow: now);

            var renewal = Assert.Single(items, i => i.FieldName == "Renewal");
            Assert.Equal(new DateOnly(2026, 10, 1), renewal.Day);
            Assert.Equal(dateFieldDays, renewal.DaysUntil);

            var cutover = Assert.Single(items, i => i.FieldName == "Cutover");
            Assert.Equal(new DateOnly(2026, 10, 1), cutover.Day);
            Assert.Equal(dateTimeFieldDays, cutover.DaysUntil);

            var firewall = Assert.Single(items, i => i.Name == "Firewall");
            Assert.Equal(new DateOnly(2026, 10, 1), firewall.Day);
            Assert.Equal(shortcutDays, firewall.DaysUntil);

            var sw = Assert.Single(items, i => i.Name == "Switch");
            Assert.Equal(new DateOnly(2026, 10, 1), sw.Day);
            Assert.Equal(offsetShortcutDays, sw.DaysUntil);
        }
    }

    [Fact]
    public async Task A_Date_Time_Is_Put_On_The_Day_It_Falls_On_In_The_Zone()
    {
        var (db, user, tenantId) = await SeedAsync("America/New_York");
        await using (db)
        {
            var type = new AssetType
            {
                TenantId = tenantId,
                Name = "Certificates",
                Fields = [new FieldDefinition { Name = "Expires", FieldType = "DateTime", IsExpiration = true, SortOrder = 0 }],
            };
            db.AssetTypes.Add(type);
            await db.SaveChangesAsync();

            var cert = new Asset { TenantId = tenantId, Name = "VPN cert", AssetTypeId = type.Id };
            // 22:30 on 1 October in New York, already 2 October in UTC.
            cert.CustomFieldValues.Add(new CustomFieldValue { FieldDefinitionId = type.Fields.Single().Id, Value = "2026-10-02T02:30:00.0000000Z" });
            db.Assets.Add(cert);
            await db.SaveChangesAsync();

            var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            var item = Assert.Single(await ExpirationEndpoints.QueryAsync(db, user, utcNow: now));
            Assert.Equal(new DateOnly(2026, 10, 1), item.Day);
            Assert.Equal(0, item.DaysUntil);
        }
    }
}

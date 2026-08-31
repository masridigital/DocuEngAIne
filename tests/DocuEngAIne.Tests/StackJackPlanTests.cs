using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

/// <summary>
/// StackJack meters per connector subscription, so the tier and allowance are detected per
/// IntegrationConnection and drive how often unattended checks may run.
/// </summary>
public class StackJackPlanTests
{
    /// <summary>Shape captured from a live stackjack_session_info response.</summary>
    public const string LiveSessionInfoFixture = """
        {"tenantId":"01KMDDF11T7705ETF2RR9WMQ2Q","connectors":[
          {"connector":"Cipp","plan":"Enterprise","monthlyCallLimit":2147483647,"hasCredentials":true},
          {"connector":"Halo","plan":"Pro","monthlyCallLimit":5000,"hasCredentials":true},
          {"connector":"Meraki","plan":"Business","monthlyCallLimit":50000,"hasCredentials":true},
          {"connector":"NinjaRMM","plan":"Free","monthlyCallLimit":100,"hasCredentials":false},
          {"connector":"UniFi","plan":"Enterprise","monthlyCallLimit":2147483647,"hasCredentials":true}],
          "toolSummary":{"total":11179,"accessible":5498}}
        """;

    private static string JsonRpcWrapped(string inner)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = inner } } },
        });

    [Fact]
    public void FindConnector_Reads_Plan_And_Limit_For_Each_Provider()
    {
        var halo = StackJackPlanDetector.FindConnector(LiveSessionInfoFixture, IntegrationProvider.Halo);
        Assert.NotNull(halo);
        Assert.Equal(StackJackPlan.Pro, halo!.Plan);
        Assert.Equal(5000, halo.MonthlyCallLimit);
        Assert.True(halo.HasCredentials);

        var meraki = StackJackPlanDetector.FindConnector(LiveSessionInfoFixture, IntegrationProvider.Meraki);
        Assert.Equal(StackJackPlan.Business, meraki!.Plan);

        var cipp = StackJackPlanDetector.FindConnector(LiveSessionInfoFixture, IntegrationProvider.Cipp);
        Assert.Equal(StackJackPlan.Enterprise, cipp!.Plan);
        Assert.Equal(int.MaxValue, cipp.MonthlyCallLimit);
    }

    [Fact]
    public void NinjaOne_Maps_To_The_NinjaRMM_Connector_Name()
    {
        // Our enum says NinjaOne; StackJack calls it NinjaRMM. A naive name match finds nothing.
        Assert.Equal("NinjaRMM", StackJackPlanDetector.ConnectorName(IntegrationProvider.NinjaOne));
        Assert.Equal("Action1", StackJackPlanDetector.ConnectorName(IntegrationProvider.Action1));
        Assert.Equal("Autotask", StackJackPlanDetector.ConnectorName(IntegrationProvider.Autotask));
        Assert.Equal("CompassOne", StackJackPlanDetector.ConnectorName(IntegrationProvider.Blackpoint));
        Assert.Equal("DefensX", StackJackPlanDetector.ConnectorName(IntegrationProvider.DefensX));
        Assert.Equal("Pax8", StackJackPlanDetector.ConnectorName(IntegrationProvider.Pax8));

        var ninja = StackJackPlanDetector.FindConnector(LiveSessionInfoFixture, IntegrationProvider.NinjaOne);
        Assert.NotNull(ninja);
        Assert.Equal(StackJackPlan.Free, ninja!.Plan);
        Assert.False(ninja.HasCredentials);
    }

    [Fact]
    public void FindConnector_Unwraps_A_JsonRpc_Tool_Response()
    {
        var wrapped = JsonRpcWrapped(LiveSessionInfoFixture);
        var halo = StackJackPlanDetector.FindConnector(wrapped, IntegrationProvider.Halo);
        Assert.Equal(StackJackPlan.Pro, halo!.Plan);
    }

    [Fact]
    public void FindConnector_Returns_Null_For_An_Absent_Or_Unmapped_Connector()
    {
        // Composio is not a StackJack connector at all.
        Assert.Null(StackJackPlanDetector.ConnectorName(IntegrationProvider.Composio));
        Assert.Null(StackJackPlanDetector.FindConnector(LiveSessionInfoFixture, IntegrationProvider.Composio));

        const string withoutHalo = """{"connectors":[{"connector":"Meraki","plan":"Pro","monthlyCallLimit":5000}]}""";
        Assert.Null(StackJackPlanDetector.FindConnector(withoutHalo, IntegrationProvider.Halo));
    }

    [Theory]
    [InlineData(StackJackPlan.Enterprise, int.MaxValue, SyncCadencePolicy.MinimumIntervalMinutes)]
    [InlineData(StackJackPlan.Business, 50_000, 44)]
    [InlineData(StackJackPlan.Pro, 5_000, 432)]
    public void Derived_Interval_Follows_The_Allowance(StackJackPlan plan, int limit, int expectedMinutes)
    {
        Assert.Equal(expectedMinutes, SyncCadencePolicy.DerivedIntervalMinutes(plan, limit));
    }

    [Fact]
    public void Richer_Plans_Are_Never_Slower_Than_Poorer_Ones()
    {
        var free = SyncCadencePolicy.DerivedIntervalMinutes(StackJackPlan.Free, 100)!.Value;
        var pro = SyncCadencePolicy.DerivedIntervalMinutes(StackJackPlan.Pro, 5_000)!.Value;
        var business = SyncCadencePolicy.DerivedIntervalMinutes(StackJackPlan.Business, 50_000)!.Value;
        var enterprise = SyncCadencePolicy.DerivedIntervalMinutes(StackJackPlan.Enterprise, int.MaxValue)!.Value;

        Assert.True(free > pro, $"free {free} should be less frequent than pro {pro}");
        Assert.True(pro > business, $"pro {pro} should be less frequent than business {business}");
        Assert.True(business > enterprise, $"business {business} should be less frequent than enterprise {enterprise}");
    }

    [Fact]
    public void A_Reported_Custom_Limit_Beats_The_Published_Tier_Number()
    {
        // StackJack can set a custom allowance on an individual subscription, so the reported
        // number wins over the tier's published figure.
        var published = SyncCadencePolicy.DerivedIntervalMinutes(StackJackPlan.Pro, null);
        var custom = SyncCadencePolicy.DerivedIntervalMinutes(StackJackPlan.Pro, 50_000);

        Assert.Equal(432, published);
        Assert.Equal(44, custom);
    }

    [Fact]
    public void Unknown_Allowance_Means_Manual_Only_Rather_Than_A_Guessed_Cadence()
    {
        Assert.Null(SyncCadencePolicy.DerivedIntervalMinutes(StackJackPlan.Unknown, null));

        var connection = NewConnection(StackJackPlan.Unknown, null);
        Assert.Null(SyncCadencePolicy.IntervalMinutesFor(connection));
        Assert.Null(SyncCadencePolicy.NextDueAt(connection));
    }

    [Fact]
    public void An_Override_May_Slow_A_Connection_Down_But_Not_Speed_It_Past_The_Plan()
    {
        var pro = NewConnection(StackJackPlan.Pro, 5_000);

        pro.SyncIntervalMinutesOverride = 1440;
        Assert.Equal(1440, SyncCadencePolicy.IntervalMinutesFor(pro));

        // Faster than the allowance affords: clamped back to the derived interval.
        pro.SyncIntervalMinutesOverride = 15;
        Assert.Equal(432, SyncCadencePolicy.IntervalMinutesFor(pro));
    }

    [Fact]
    public void NextDueAt_Is_Now_For_A_Never_Synced_Connection_And_Null_When_Disabled()
    {
        var connection = NewConnection(StackJackPlan.Enterprise, int.MaxValue);
        connection.LastSyncAt = null;

        var due = SyncCadencePolicy.NextDueAt(connection);
        Assert.NotNull(due);
        Assert.True(due <= DateTimeOffset.UtcNow.AddSeconds(5), "a never-synced connection is due immediately");

        connection.LastSyncAt = DateTimeOffset.UtcNow;
        var next = SyncCadencePolicy.NextDueAt(connection)!.Value;
        Assert.True(next > DateTimeOffset.UtcNow, "a just-synced connection is not due again yet");

        connection.IsEnabled = false;
        Assert.Null(SyncCadencePolicy.NextDueAt(connection));
    }

    [Fact]
    public void NextDueAt_Uses_The_Later_Of_Success_And_Attempt()
    {
        var now = DateTimeOffset.UtcNow;
        var connection = NewConnection(StackJackPlan.Enterprise, int.MaxValue); // 15-minute floor

        // A connection whose runs keep failing has an old (or no) LastSyncAt but a fresh
        // LastAttemptAt — it must wait out its interval, not retry on the next poll tick.
        connection.LastSyncAt = null;
        connection.LastAttemptAt = now.AddMinutes(-1);
        var due = SyncCadencePolicy.NextDueAt(connection, now)!.Value;
        Assert.True(due > now, "a just-attempted connection is not due again yet");
        Assert.Equal(now.AddMinutes(14), due, TimeSpan.FromSeconds(5));

        connection.LastSyncAt = now.AddHours(-3);
        Assert.Equal(now.AddMinutes(14), SyncCadencePolicy.NextDueAt(connection, now)!.Value, TimeSpan.FromSeconds(5));

        // A stale attempt never delays a fresher success.
        connection.LastSyncAt = now.AddMinutes(-2);
        connection.LastAttemptAt = now.AddHours(-3);
        Assert.Equal(now.AddMinutes(13), SyncCadencePolicy.NextDueAt(connection, now)!.Value, TimeSpan.FromSeconds(5));
    }

    private static IntegrationConnection NewConnection(StackJackPlan plan, int? limit) => new()
    {
        TenantId = Guid.NewGuid(),
        Provider = IntegrationProvider.Halo,
        DisplayName = "Halo",
        StackJackPlan = plan,
        MonthlyCallLimit = limit,
    };
}

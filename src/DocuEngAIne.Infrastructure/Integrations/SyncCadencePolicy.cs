using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Turns a StackJack connector allowance into a scheduled-check interval.
///
/// StackJack has <em>no per-minute or burst limit</em> — the monthly call allowance is the only
/// throughput control — so cadence is a budgeting question, not a rate-limiting one: how much of the
/// cycle's allowance are we willing to spend on unattended polling?
///
/// The answer here is <see cref="ScheduledShareOfAllowance"/> (20%), leaving the rest for interactive
/// and agent-driven calls, which are the point of the integration. The interval is derived from the
/// reported allowance rather than the tier name, because StackJack can set a custom limit on an
/// individual subscription and the reported number is authoritative.
/// </summary>
public static class SyncCadencePolicy
{
    /// <summary>
    /// Tool calls a single sync run costs. Runs are paginated, so this is an estimate: a Halo pull is
    /// one call per 50 clients, Ninja and Meraki one per cursor page. Ten covers a typical MSP tenant
    /// with headroom; revisit if SyncRun ever records its real call count.
    /// </summary>
    public const int EstimatedCallsPerSyncRun = 10;

    /// <summary>Share of the cycle allowance budgeted for unattended scheduled checks.</summary>
    public const double ScheduledShareOfAllowance = 0.20;

    /// <summary>StackJack meters per billing cycle, not per calendar month; 30 days is the working approximation.</summary>
    public const int BillingCycleMinutes = 30 * 24 * 60;

    /// <summary>Floor. Even unlimited plans do not need to poll a PSA more often than this.</summary>
    public const int MinimumIntervalMinutes = 15;

    /// <summary>Ceiling. Below roughly one run a month, scheduling is not meaningfully different from manual.</summary>
    public const int MaximumIntervalMinutes = BillingCycleMinutes;

    /// <summary>
    /// Interval for a connection, honouring an explicit override but never letting it out-run the plan.
    /// Null means "manual only" — the allowance is unknown, so we decline to guess a cadence.
    /// </summary>
    public static int? IntervalMinutesFor(IntegrationConnection connection)
    {
        var derived = DerivedIntervalMinutes(connection.StackJackPlan, connection.MonthlyCallLimit);
        if (derived is null)
            return connection.SyncIntervalMinutesOverride is int manual && manual > 0
                ? Math.Clamp(manual, MinimumIntervalMinutes, MaximumIntervalMinutes)
                : null;

        if (connection.SyncIntervalMinutesOverride is not int over || over <= 0)
            return derived;

        // An override may be slower than the plan allows, never faster.
        return Math.Clamp(Math.Max(over, derived.Value), MinimumIntervalMinutes, MaximumIntervalMinutes);
    }

    /// <summary>Interval implied by the allowance alone. Null when the allowance is unknown.</summary>
    public static int? DerivedIntervalMinutes(StackJackPlan plan, int? monthlyCallLimit)
    {
        var limit = monthlyCallLimit ?? PublishedAllowance(plan);
        if (limit is null or <= 0)
            return null;

        if (limit >= StackJackPlanDetector.UnlimitedCallLimit || plan == StackJackPlan.Enterprise)
            return MinimumIntervalMinutes;

        var budget = limit.Value * ScheduledShareOfAllowance;
        var runsPerCycle = budget / EstimatedCallsPerSyncRun;
        if (runsPerCycle < 1)
            return MaximumIntervalMinutes;

        var minutes = (int)Math.Ceiling(BillingCycleMinutes / runsPerCycle);
        return Math.Clamp(minutes, MinimumIntervalMinutes, MaximumIntervalMinutes);
    }

    /// <summary>
    /// The tier's published allowance, used only when StackJack has not reported a concrete number.
    /// The TechTribe perk raises Pro to 7,500 and Business to 75,000, so these are the conservative floor.
    /// </summary>
    public static int? PublishedAllowance(StackJackPlan plan) => plan switch
    {
        StackJackPlan.Free => 100,
        StackJackPlan.Pro => 5_000,
        StackJackPlan.Business => 50_000,
        StackJackPlan.Enterprise => StackJackPlanDetector.UnlimitedCallLimit,
        _ => null,
    };

    /// <summary>When the next scheduled check is due, or null when the connection is manual-only or disabled.</summary>
    public static DateTimeOffset? NextDueAt(IntegrationConnection connection)
    {
        if (!connection.IsEnabled)
            return null;

        var interval = IntervalMinutesFor(connection);
        if (interval is null)
            return null;

        // Never synced: due now.
        return (connection.LastSyncAt ?? DateTimeOffset.UtcNow.AddMinutes(-interval.Value))
            .AddMinutes(interval.Value);
    }
}

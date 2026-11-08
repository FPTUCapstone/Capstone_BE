namespace TripMate.Infrastructure.Services;

/// <summary>Poll interval for the DAILY-PAYOUT-TRANSFER background services. Polling more often
/// than once a day is always safe: <c>DailyPayoutRunProcessor.RunIfDueAsync</c> and
/// <c>DailyPayoutRetrySweepProcessor.SweepAsync</c> are both idempotent no-ops when there is
/// nothing due (PD-11, PD-16).</summary>
public sealed class DailyPayoutOptions
{
    public const string SectionName = "DailyPayout";

    public TimeSpan RunPollInterval { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan RetrySweepPollInterval { get; set; } = TimeSpan.FromMinutes(5);
}

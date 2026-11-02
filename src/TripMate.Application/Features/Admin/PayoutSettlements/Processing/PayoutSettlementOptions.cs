using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Admin.PayoutSettlements.Processing;

/// <summary>Tuning of the UC-65 payout-settlement worker.</summary>
public sealed class PayoutSettlementOptions
{
    public const string SectionName = "PayoutSettlement";

    private static readonly TimeSpan[] DefaultPollBackoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(60),
    ];

    private static readonly TimeSpan[] DefaultSubmitRetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
    ];

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    public int BatchSize { get; init; } = 20;

    public TimeSpan ProcessingLeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Submit attempts before the worker resolves the payout by reference lookup.</summary>
    public int MaxSubmitAttempts { get; init; } = 3;

    /// <summary>How long after the first accepted submission the worker keeps polling.</summary>
    public TimeSpan PollDeadline { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Increasing gaps between polls; the last value repeats until the deadline. Empty means
    /// the default schedule (30 s, 1 m, 2 m, 5 m, 10 m, 30 m, then every 60 m).</summary>
    public TimeSpan[] PollBackoff { get; init; } = [];

    /// <summary>Delay before submit attempt 2, 3, ...; the last value repeats. Empty means 1 m, 5 m.</summary>
    public TimeSpan[] SubmitRetryDelays { get; init; } = [];

    public IReadOnlyList<TimeSpan> EffectivePollBackoff => PollBackoff.Length > 0 ? PollBackoff : DefaultPollBackoff;

    public IReadOnlyList<TimeSpan> EffectiveSubmitRetryDelays =>
        SubmitRetryDelays.Length > 0 ? SubmitRetryDelays : DefaultSubmitRetryDelays;

    /// <summary>Gap before the poll that follows <paramref name="pollsDone"/> completed polls.</summary>
    public TimeSpan PollDelayAfter(int pollsDone)
    {
        var schedule = EffectivePollBackoff;
        return schedule[Math.Clamp(pollsDone, 0, schedule.Count - 1)];
    }

    /// <summary>Gap before the next submit attempt after <paramref name="attemptsMade"/> attempts.</summary>
    public TimeSpan SubmitRetryDelayAfter(int attemptsMade)
    {
        var schedule = EffectiveSubmitRetryDelays;
        return schedule[Math.Clamp(attemptsMade - 1, 0, schedule.Count - 1)];
    }
}

public sealed class PayoutSettlementOptionsValidator : IValidateOptions<PayoutSettlementOptions>
{
    public ValidateOptionsResult Validate(string? name, PayoutSettlementOptions options)
    {
        var failures = new List<string>();
        if (options.SweepInterval <= TimeSpan.Zero)
            failures.Add("PayoutSettlement:SweepInterval must be positive.");
        if (options.BatchSize <= 0)
            failures.Add("PayoutSettlement:BatchSize must be positive.");
        if (options.ProcessingLeaseDuration <= TimeSpan.Zero)
            failures.Add("PayoutSettlement:ProcessingLeaseDuration must be positive.");
        if (options.MaxSubmitAttempts <= 0)
            failures.Add("PayoutSettlement:MaxSubmitAttempts must be positive.");
        if (options.PollDeadline <= TimeSpan.Zero)
            failures.Add("PayoutSettlement:PollDeadline must be positive.");

        ValidateSchedule(options.PollBackoff, "PollBackoff", mustNotDecrease: true, failures);
        ValidateSchedule(options.SubmitRetryDelays, "SubmitRetryDelays", mustNotDecrease: false, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSchedule(TimeSpan[] schedule, string name, bool mustNotDecrease, List<string> failures)
    {
        if (schedule.Any(delay => delay <= TimeSpan.Zero))
            failures.Add($"PayoutSettlement:{name} entries must be positive.");
        else if (mustNotDecrease && schedule.Zip(schedule.Skip(1), (a, b) => b < a).Any(decreasing => decreasing))
            failures.Add($"PayoutSettlement:{name} must not decrease.");
    }
}
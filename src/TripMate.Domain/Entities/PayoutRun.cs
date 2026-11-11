using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.PayoutRun (DAILY-PAYOUT-TRANSFER, Task 2). One row per calendar day the daily
/// 10:00 settlement job has started (SRS v11 BR-141, UC-71). RunDate is unique at the database
/// level, so a job that starts twice for the same day (a crash/restart catch-up, PD-16) cannot
/// create settlements twice (PD-11).
/// </summary>
public class PayoutRun : BaseEntity
{
    public const string StatusRunning = "Running";
    public const string StatusCompleted = "Completed";
    public const string StatusFailed = "Failed";

    public static readonly string[] AllStatuses = [StatusRunning, StatusCompleted, StatusFailed];

    private PayoutRun()
    {
    }

    public DateOnly RunDate { get; private set; }

    public string Status { get; private set; } = StatusRunning;

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PayoutRun Start(DateOnly runDate, DateTimeOffset startedAtUtc) => new()
    {
        RunDate = runDate,
        Status = StatusRunning,
        StartedAtUtc = startedAtUtc,
        CreatedAtUtc = startedAtUtc,
    };

    public void Complete(DateTimeOffset completedAtUtc)
    {
        Status = StatusCompleted;
        CompletedAtUtc = completedAtUtc;
    }

    public void Fail(DateTimeOffset completedAtUtc)
    {
        Status = StatusFailed;
        CompletedAtUtc = completedAtUtc;
    }
}
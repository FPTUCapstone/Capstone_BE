using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Immutable reporting-period gate. A report can only be generated after its period is closed.</summary>
public sealed class ClosedStatisticalPeriod : BaseEntity
{
    public string PeriodKey { get; private set; } = string.Empty;
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public DateTimeOffset ClosedAtUtc { get; private set; }
    public long? ClosedByUserId { get; private set; }

    public static ClosedStatisticalPeriod CreateSystemClosed(
        string periodKey,
        DateOnly startDate,
        DateOnly endDate,
        DateTimeOffset closedAtUtc) => new()
    {
        PeriodKey = periodKey,
        StartDate = startDate,
        EndDate = endDate,
        ClosedAtUtc = closedAtUtc,
    };
}

using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Persisted result for one closed-period criteria set. The first successful generation is the
/// accounting snapshot used by all later views and exports for the same criteria.
/// </summary>
public sealed class StatisticalReportSnapshot : BaseEntity
{
    public string CriteriaKey { get; private set; } = string.Empty;
    public string PeriodKey { get; private set; } = string.Empty;
    public string ReportType { get; private set; } = string.Empty;
    public string BookingType { get; private set; } = string.Empty;
    public long? OperatorUserId { get; private set; }
    public long? DestinationId { get; private set; }
    public string PayloadJson { get; private set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; private set; }

    public static StatisticalReportSnapshot Create(
        string criteriaKey, string periodKey, string reportType, string bookingType,
        long? operatorUserId, long? destinationId, string payloadJson, DateTimeOffset generatedAtUtc) => new()
    {
        CriteriaKey = criteriaKey,
        PeriodKey = periodKey,
        ReportType = reportType,
        BookingType = bookingType,
        OperatorUserId = operatorUserId,
        DestinationId = destinationId,
        PayloadJson = payloadJson,
        GeneratedAtUtc = generatedAtUtc.ToUniversalTime(),
    };
}

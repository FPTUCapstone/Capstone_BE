using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Idempotency and audit record for an administrator's UC-67 file export request.</summary>
public sealed class StatisticalReportExportOperation : BaseEntity
{
    public Guid OperationId { get; private set; }
    public string CriteriaKey { get; private set; } = string.Empty;
    public string Format { get; private set; } = string.Empty;
    public long RequestedByUserId { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }

    public static StatisticalReportExportOperation Create(Guid operationId, string criteriaKey, string format, long requestedByUserId, DateTimeOffset requestedAtUtc) => new()
    {
        OperationId = operationId,
        CriteriaKey = criteriaKey,
        Format = format,
        RequestedByUserId = requestedByUserId,
        RequestedAtUtc = requestedAtUtc.ToUniversalTime(),
    };
}

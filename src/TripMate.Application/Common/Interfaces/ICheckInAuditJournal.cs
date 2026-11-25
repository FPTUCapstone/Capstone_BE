using TripMate.Application.Features.Operator.CheckIn.Audit;

namespace TripMate.Application.Common.Interfaces;

public interface ICheckInAuditJournal
{
    Task<CheckInAuditWriteResult> AppendAsync(
        CheckInAuditEvent auditEvent,
        CancellationToken cancellationToken);
}
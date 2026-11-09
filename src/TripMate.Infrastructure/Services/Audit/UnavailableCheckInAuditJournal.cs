using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.CheckIn.Audit;

namespace TripMate.Infrastructure.Services.Audit;

internal sealed class UnavailableCheckInAuditJournal : ICheckInAuditJournal
{
    public Task<CheckInAuditWriteResult> AppendAsync(
        CheckInAuditEvent auditEvent,
        CancellationToken cancellationToken) =>
        Task.FromResult(CheckInAuditWriteResult.Unavailable);
}
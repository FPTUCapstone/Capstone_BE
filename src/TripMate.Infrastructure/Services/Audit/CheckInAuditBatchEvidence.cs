using TripMate.Application.Features.Operator.CheckIn.Audit;

namespace TripMate.Infrastructure.Services.Audit;

internal enum CheckInAuditAppendDisposition
{
    Created,
    Existing,
    Conflict,
}

internal static class CheckInAuditBatchEvidence
{
    public static CheckInAuditWriteResult Evaluate(
        long connectionIdBefore,
        long connectionIdAfter,
        CheckInAuditAppendDisposition disposition,
        long localFsyncCount)
    {
        if (disposition == CheckInAuditAppendDisposition.Conflict)
        {
            return CheckInAuditWriteResult.Conflict;
        }

        return connectionIdBefore > 0 &&
               connectionIdBefore == connectionIdAfter &&
               localFsyncCount >= 1
            ? CheckInAuditWriteResult.Confirmed
            : CheckInAuditWriteResult.Uncertain;
    }
}
using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency.AddEvidence;

public sealed record GetEmergencyTourEvidenceQuery(long TourId, long ScheduleId)
    : IRequest<Result<EmergencyTourEvidenceResponse>>;

public sealed record EmergencyTourEvidenceResponse(
    long OperationId,
    string? EvidenceUrl,
    string? AuthorityReference,
    string? EvidenceReference,
    string EvidenceProvenance,
    bool EvidenceAddedLater,
    string EvidenceVersion);

public sealed class GetEmergencyTourEvidenceQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<GetEmergencyTourEvidenceQuery, Result<EmergencyTourEvidenceResponse>>
{
    public async Task<Result<EmergencyTourEvidenceResponse>> Handle(
        GetEmergencyTourEvidenceQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId
            || currentUser.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<EmergencyTourEvidenceResponse>(
                EmergencyTourErrorCodes.Forbidden, "Cần quyền đơn vị điều hành tour.");
        }

        if (!await EmergencyOperatorAccess.IsEligibleOperatorAsync(dbContext, operatorId, cancellationToken))
        {
            return Result.Failure<EmergencyTourEvidenceResponse>(
                EmergencyTourErrorCodes.Forbidden,
                "Cần tài khoản đơn vị điều hành đang hoạt động và đã được duyệt.");
        }

        var operation = await dbContext.EmergencyTourCancellationOperations.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TourScheduleId == request.ScheduleId
                && candidate.TourSchedule.TourId == request.TourId
                && candidate.TourSchedule.Tour.OperatorUserId == operatorId, cancellationToken);
        if (operation is null)
        {
            return Result.Failure<EmergencyTourEvidenceResponse>(
                EmergencyTourErrorCodes.NotFound,
                "Không tìm thấy quyết định khẩn cấp của lịch khởi hành.");
        }

        return Result.Success(new EmergencyTourEvidenceResponse(
            operation.Id,
            operation.EvidenceUrl,
            operation.AuthorityReference,
            operation.EvidenceReference,
            operation.EvidenceProvenance,
            operation.EvidenceAddedLater,
            EmergencyEvidenceVersion.Of(operation)));
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency.AddEvidence;

public sealed class AddEmergencyTourEvidenceCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<AddEmergencyTourEvidenceCommand, Result<AddEmergencyTourEvidenceResponse>>
{
    public async Task<Result<AddEmergencyTourEvidenceResponse>> Handle(
        AddEmergencyTourEvidenceCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId
            || currentUser.Role != nameof(UserRole.TourOperator))
            return Failure(EmergencyTourErrorCodes.Forbidden, "Cần quyền đơn vị điều hành tour.");

        return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            bool operatorIsEligible = await EmergencyOperatorAccess.IsEligibleOperatorAsync(
                dbContext, operatorId, token);
            if (!operatorIsEligible)
                return Failure(EmergencyTourErrorCodes.Forbidden,
                    "Cần tài khoản đơn vị điều hành đang hoạt động và đã được duyệt.");

            var operation = await dbContext.EmergencyTourCancellationOperations
                .Include(candidate => candidate.TourSchedule)
                .ThenInclude(schedule => schedule.Tour)
                .SingleOrDefaultAsync(candidate => candidate.TourScheduleId == request.ScheduleId
                    && candidate.TourSchedule.TourId == request.TourId
                    && candidate.TourSchedule.Tour.OperatorUserId == operatorId, token);
            if (operation is null)
                return Failure(EmergencyTourErrorCodes.NotFound,
                    "Không tìm thấy quyết định khẩn cấp của lịch khởi hành.");

            // Required, not optional: a save replaces the whole set, so one not based on the
            // current evidence could silently erase it.
            if (request.ExpectedVersion != EmergencyEvidenceVersion.Of(operation))
            {
                return Failure(EmergencyTourErrorCodes.EvidenceStale,
                    "Bằng chứng vừa được thay đổi. Vui lòng xem lại nội dung mới nhất trước khi lưu.");
            }

            bool changed;
            string auditActionType;
            if (request.WithdrawEvidence)
            {
                changed = operation.WithdrawOperatorEvidence();
                auditActionType = AuditActionTypes.TourEmergencyEvidenceWithdrawn;
            }
            else
            {
                changed = operation.AddOperatorEvidence(
                    request.EvidenceReference, request.EvidenceUrl, request.AuthorityReference);
                auditActionType = AuditActionTypes.TourEmergencyEvidenceAdded;
            }

            DateTimeOffset now = clock.UtcNow;
            if (changed)
            {
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorId,
                    auditActionType,
                    AuditEntityTypes.TourSchedule, operation.TourScheduleId, now,
                    AuditOutcome.Success, "Operator supplied supplementary emergency evidence.",
                    afterData: JsonSerializer.Serialize(new
                    {
                        PermissionPath = EmergencyTourAuditValues.OwningActiveApprovedTourOperator,
                        operation.CommandId,
                        operation.EvidenceProvenance,
                        operation.EvidenceAddedLater,
                        EvidenceReferenceHash = Hash(operation.EvidenceReference),
                        EvidenceUrlHash = Hash(operation.EvidenceUrl),
                        AuthorityReferenceHash = Hash(operation.AuthorityReference),
                    })));
                await dbContext.SaveChangesAsync(token);
            }

            return Result.Success(new AddEmergencyTourEvidenceResponse(
                operation.Id, operation.EvidenceProvenance,
                operation.EvidenceAddedLater, now));
        }, cancellationToken);
    }

    private static string? Hash(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Result<AddEmergencyTourEvidenceResponse> Failure(string code, string message) =>
        Result.Failure<AddEmergencyTourEvidenceResponse>(code, message);
}

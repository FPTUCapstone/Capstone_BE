using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Emergency.CancelBeforeDeparture;
using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Application.Features.Operator.Tours.Emergency.EmergencyStopActive;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency;

public sealed class EmergencyTourCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IPasswordHasherService passwordHasher,
    IWeatherSafetyReviewNotifier reviewNotifier)
{
    public async Task<Result<EmergencyTourActionResponse>> Handle(
        CancelBeforeDepartureCommand request,
        CancellationToken cancellationToken)
    {
        var validation = await new CancelBeforeDepartureCommandValidator()
            .ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Failure(EmergencyTourErrorCodes.InvalidRequest, validation.Errors[0].ErrorMessage);
        return await ExecuteAsync(
            request.TourId, request.ScheduleId, request.CommandId, request.ImpactVersion,
            EmergencyTourCancellationOperation.CancelBeforeDepartureAction, request.Reason, request.ReasonDetails,
            null, request.EmergencyContact, request.EvidenceReference, request.EvidenceUrl,
            request.AuthorityReference, request.AddEvidenceLater, null, cancellationToken);
    }

    public async Task<Result<EmergencyTourActionResponse>> Handle(
        EmergencyStopActiveCommand request,
        CancellationToken cancellationToken)
    {
        var validation = await new EmergencyStopActiveCommandValidator()
            .ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Failure(EmergencyTourErrorCodes.InvalidRequest, validation.Errors[0].ErrorMessage);
        return await ExecuteAsync(
            request.TourId, request.ScheduleId, request.CommandId, request.ImpactVersion,
            EmergencyTourCancellationOperation.EmergencyStopActiveAction, request.Reason, request.ReasonDetails,
            request.ImmediateInstructions, request.EmergencyContact, request.EvidenceReference,
            request.EvidenceUrl, request.AuthorityReference, request.AddEvidenceLater,
            request.CurrentPassword, cancellationToken);
    }

    private async Task<Result<EmergencyTourActionResponse>> ExecuteAsync(
        long tourId,
        long scheduleId,
        Guid commandId,
        string impactVersion,
        string action,
        EmergencyTourReason reason,
        string reasonDetails,
        string? immediateInstructions,
        string? emergencyContact,
        string? evidenceReference,
        string? evidenceUrl,
        string? authorityReference,
        bool addEvidenceLater,
        string? currentPassword,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId
            || currentUser.Role != nameof(UserRole.TourOperator))
            return Failure(EmergencyTourErrorCodes.Forbidden, "Cần quyền đơn vị điều hành tour.");

        bool isPreStart = action == EmergencyTourCancellationOperation.CancelBeforeDepartureAction;

        // Step-up authentication for active stop: verify password before entering transaction.
        // Password is never logged, persisted, or included in idempotency hash.
        if (!isPreStart)
        {
            if (string.IsNullOrWhiteSpace(currentPassword))
                return Failure(EmergencyTourErrorCodes.InvalidPassword, "Vui lòng nhập mật khẩu hiện tại.");

            var operatorUser = await dbContext.Users.AsNoTracking()
                .SingleOrDefaultAsync(u => u.Id == operatorId, cancellationToken);
            if (operatorUser?.PasswordHash is null || !passwordHasher.Verify(currentPassword, operatorUser.PasswordHash))
            {
                dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                    operatorUser?.Id,
                    AuditActionTypes.TourEmergencyStopReauthenticationFailed,
                    AuditEntityTypes.TourSchedule,
                    scheduleId,
                    clock.UtcNow,
                    AuditOutcome.Failure,
                    "Emergency stop re-authentication failed."));
                await dbContext.SaveChangesAsync(cancellationToken);
                return Failure(EmergencyTourErrorCodes.InvalidPassword, "Mật khẩu hiện tại không đúng.");
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            long[] closedReviewIds = [];
            try
            {
                Result<EmergencyTourActionResponse> result = await dbContext.ExecuteInSerializableTransactionAsync(async token =>
                {
            closedReviewIds = [];
            bool operatorIsEligible = await EmergencyOperatorAccess.IsEligibleOperatorAsync(
                dbContext, operatorId, token);
            if (!operatorIsEligible)
                return Failure(EmergencyTourErrorCodes.Forbidden,
                    "Cần tài khoản đơn vị điều hành đang hoạt động và đã được duyệt.");

            TourSchedule? schedule = await dbContext.TourSchedules
                .Include(candidate => candidate.Tour)
                .SingleOrDefaultAsync(candidate => candidate.Id == scheduleId
                    && candidate.TourId == tourId
                    && candidate.Tour.OperatorUserId == operatorId, token);
            if (schedule is null)
                return Failure(EmergencyTourErrorCodes.NotFound, "Không tìm thấy lịch khởi hành.");
            DateTimeOffset now = clock.UtcNow;

            string requestHash = ComputeRequestHash(tourId, scheduleId, commandId, impactVersion,
                action, reason, reasonDetails, immediateInstructions, emergencyContact,
                evidenceReference, evidenceUrl, authorityReference, addEvidenceLater);
            EmergencyTourCancellationOperation? replay = await dbContext
                .EmergencyTourCancellationOperations.AsNoTracking()
                .SingleOrDefaultAsync(operation => operation.ActorUserId == operatorId
                    && operation.CommandId == commandId, token);
            if (replay is not null)
                return replay.RequestHash == requestHash
                    ? Result.Success(ToResponse(replay))
                    : Failure(EmergencyTourErrorCodes.IdempotencyPayloadMismatch,
                        "Yêu cầu này đã được gửi trước đó với nội dung khác.");

            EmergencyTourCancellationOperation? prior = await dbContext
                .EmergencyTourCancellationOperations.AsNoTracking()
                .SingleOrDefaultAsync(operation => operation.TourScheduleId == scheduleId, token);
            if (prior is not null)
                return Failure(EmergencyTourErrorCodes.AlreadyTerminal,
                    "Lịch khởi hành này đã có quyết định cuối cùng.");
            if (schedule.Status == TourScheduleStatus.Completed || clock.UtcNow >= schedule.EndAtUtc)
                return Failure(EmergencyTourErrorCodes.AlreadyCompleted,
                    "Lịch khởi hành đã hoàn thành nên không thể hủy hoặc dừng.");
            if (schedule.Status != TourScheduleStatus.Scheduled)
                return Failure(EmergencyTourErrorCodes.AlreadyTerminal,
                    "Lịch khởi hành này đã có quyết định cuối cùng.");

            EmergencyTourImpactSnapshot impact = await EmergencyTourImpact.LoadAsync(
                dbContext, schedule, token);

            if (impact.HasUnverifiedSessions)
                return Failure(EmergencyTourErrorCodes.TripSessionLinkUnverified,
                    "Có phiên đi tour chưa xác minh nên không thể thực hiện thao tác này.");

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(impact.Version), Encoding.UTF8.GetBytes(impactVersion)))
                return Failure(EmergencyTourErrorCodes.ConcurrentStateChanged,
                    "Tác động của lịch khởi hành đã thay đổi. Vui lòng xem lại trước khi xác nhận.");

            string beforeData = JsonSerializer.Serialize(new
            {
                PermissionPath = EmergencyTourAuditValues.OwningActiveApprovedTourOperator,
                CorrelationId = commandId,
                Schedule = new { schedule.Id, Status = schedule.Status.ToString() },
                Bookings = impact.Bookings.Select(booking => new
                {
                    booking.Id,
                    Status = booking.Status.ToString(),
                    PaymentStatus = booking.PaymentStatus.ToString(),
                    booking.CancellationPolicySnapshot,
                }),
                Sessions = impact.ActiveSessions.Select(session => new
                {
                    session.Id,
                    session.FsmState,
                }),
            });
            string composedReason = $"{reason}: {reasonDetails.Trim()}";
            string bookingReason = composedReason.Length <= Booking.CancelReasonMaxLength
                ? composedReason
                : composedReason[..Booking.CancelReasonMaxLength];
            string auditReason = composedReason.Length <= AuditLog.ReasonMaxLength
                ? composedReason
                : composedReason[..AuditLog.ReasonMaxLength];

            bool stateChanged = await EmergencyDepartureStateMutator.MutateAsync(
                dbContext, schedule, impact.Bookings, impact.ActiveSessions,
                isPreStart, now, bookingReason, reason.ToString(), token);
            if (!stateChanged)
                return Failure(EmergencyTourErrorCodes.WrongActionWindow,
                    "Thao tác đã chọn không còn phù hợp với thời gian của lịch khởi hành.");

            var operation = EmergencyTourCancellationOperation.Create(
                schedule.Id, operatorId, commandId, requestHash, action, reason.ToString(),
                reasonDetails, immediateInstructions, emergencyContact, evidenceReference,
                impact.Bookings.Length, now,
                evidenceUrl: evidenceUrl,
                authorityReference: authorityReference,
                evidenceProvenance: EmergencyEvidenceProvenance.OperatorProvided,
                evidenceAddedLater: addEvidenceLater);
            dbContext.EmergencyTourCancellationOperations.Add(operation);

            AddNotifications(schedule, impact.Bookings, action, now);

            List<WeatherSafetyReview> openReviews = await dbContext.WeatherSafetyReviews
                .Where(review => review.TourScheduleId == schedule.Id
                    && review.Status != WeatherSafetyReview.StatusClosed)
                .ToListAsync(token);
            openReviews.ForEach(review => review.Close());
            closedReviewIds = [.. openReviews.Select(review => review.Id)];

            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorId,
                isPreStart ? AuditActionTypes.TourEmergencyCancelled : AuditActionTypes.TourEmergencyStopped,
                AuditEntityTypes.TourSchedule, schedule.Id, now, AuditOutcome.Success,
                auditReason,
                beforeData: beforeData,
                afterData: JsonSerializer.Serialize(new
                {
                    PermissionPath = EmergencyTourAuditValues.OwningActiveApprovedTourOperator,
                    CorrelationId = operation.CommandId,
                    Schedule = new { schedule.Id, Status = schedule.Status.ToString() },
                    Bookings = impact.Bookings.Select(booking => new
                    {
                        booking.Id,
                        Status = booking.Status.ToString(),
                    }),
                    Sessions = impact.ActiveSessions.Select(session => new
                    {
                        session.Id,
                        session.FsmState,
                    }),
                    operation.AffectedBookingCount,
                    ClosedReviewIds = closedReviewIds,
                    EvidenceProvenance = operation.EvidenceProvenance,
                    operation.EvidenceAddedLater,
                    EvidenceReferenceHash = Hash(operation.EvidenceReference),
                    EvidenceUrlHash = Hash(operation.EvidenceUrl),
                    AuthorityReferenceHash = Hash(operation.AuthorityReference),
                })));
            await dbContext.SaveChangesAsync(token);
                    return Result.Success(ToResponse(operation));
                }, cancellationToken);

                // Only a committed decision reaches this point, and a replay closes nothing.
                foreach (long reviewId in closedReviewIds)
                {
                    await reviewNotifier.NotifyChangedAsync(
                        operatorId,
                        new WeatherSafetyReviewChanged(
                            reviewId, scheduleId, WeatherSafetyReviewChangeTypes.Closed, clock.UtcNow),
                        cancellationToken);
                }
                return result;
            }
            catch (Exception exception) when (attempt < 3
                && (exception is DbUpdateException || dbContext.IsDeadlock(exception)))
            {
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private void AddNotifications(
        TourSchedule schedule,
        IReadOnlyCollection<Booking> bookings,
        string action,
        DateTimeOffset now)
    {
        bool stopped = action == EmergencyTourCancellationOperation.EmergencyStopActiveAction;
        string type = stopped
            ? Notification.EmergencyDepartureStoppedType
            : Notification.EmergencyDepartureCancelledType;
        foreach (Booking booking in bookings)
        {
            EmergencyPushNotices.AddBookingNotice(
                dbContext, booking.TravelerUserId, booking.Id, type, now);
        }
    }

    private static string? Hash(string? value) => value is null
        ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ComputeRequestHash(long tourId, long scheduleId, Guid commandId,
        string impactVersion, string action, EmergencyTourReason reason, string reasonDetails,
        string? immediateInstructions, string? emergencyContact, string? evidenceReference,
        string? evidenceUrl, string? authorityReference, bool addEvidenceLater)
    {
        // Notice: currentPassword is intentionally omitted from the request hash.
        string canonical = string.Join('\n', tourId, scheduleId, commandId, impactVersion,
            action, reason, reasonDetails.Trim(), immediateInstructions?.Trim() ?? string.Empty,
            emergencyContact?.Trim() ?? string.Empty, evidenceReference?.Trim() ?? string.Empty,
            evidenceUrl?.Trim() ?? string.Empty, authorityReference?.Trim() ?? string.Empty,
            addEvidenceLater ? "1" : "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static EmergencyTourActionResponse ToResponse(
        EmergencyTourCancellationOperation operation) => new(
            operation.Id,
            operation.TourScheduleId,
            operation.Action,
            operation.Action == EmergencyTourCancellationOperation.CancelBeforeDepartureAction
                ? TourScheduleStatus.Cancelled.ToString()
                : TourScheduleStatus.EmergencyStopped.ToString(),
            operation.AffectedBookingCount,
            operation.CommittedAtUtc);

    private static Result<EmergencyTourActionResponse> Failure(string code, string message) =>
        Result.Failure<EmergencyTourActionResponse>(code, message);
}

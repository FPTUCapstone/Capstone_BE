using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.CheckIn.Audit;
using TripMate.Application.Features.Operator.CheckIn.Common;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.CheckIn.Scan;

/// <summary>
/// UC-43 Tour Operator traveler check-in by QR code or manual booking code.
/// Implements D1 (dynamic participant aggregation), D3 (strict single-use ticket enforcement),
/// serializable concurrency protection, and rejection audit logging.
/// </summary>
public sealed class CheckInTravelerCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    ICheckInAuditJournal auditJournal,
    ITicketQrCodec ticketQrCodec,
    IDateTimeProvider clock,
    ILogger<CheckInTravelerCommandHandler>? logger = null)
    : IRequestHandler<CheckInTravelerCommand, Result<CheckInResultDto>>
{
    private const int MaxConcurrencyAttempts = 5;
    private readonly ILogger<CheckInTravelerCommandHandler> _logger =
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CheckInTravelerCommandHandler>.Instance;

    public async Task<Result<CheckInResultDto>> Handle(
        CheckInTravelerCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not long claimedOperatorId || claimedOperatorId <= 0
            || currentUserService.Role != nameof(UserRole.TourOperator))
        {
            return Result.Failure<CheckInResultDto>(
                TripMate.Application.Features.Operator.Tours.Common.OperatorTourErrorCodes.AccessRequired,
                OperatorBookingAccess.AccessRequiredMessage);
        }

        var nowUtc = clock.UtcNow;
        var scanAttemptId = Guid.NewGuid();
        var admission = CheckInAuditEvent.Admission(
            Guid.NewGuid(), scanAttemptId, nowUtc, claimedOperatorId, request.ScheduleId);
        if (!await IsDurablyConfirmedAsync(admission, cancellationToken))
        {
            return JournalUnavailable();
        }

        // 1. Verify operator access (BR-108) only after durable admission.
        var access = await OperatorBookingAccess.ResolveOperatorIdAsync(
            dbContext, currentUserService, cancellationToken);
        if (access.IsFailure)
        {
            return await RejectAsync(
                scanAttemptId, claimedOperatorId, request.ScheduleId, null, nowUtc,
                access.ErrorCode!, access.ErrorMessage!);
        }

        var operatorId = access.Value;

        // 2. Validate schedule existence and operator tour ownership (BR-108)
        var schedule = await dbContext.TourSchedules.AsNoTracking()
            .Where(s => s.Id == request.ScheduleId)
            .Select(s => new
            {
                s.Id,
                s.TourId,
                TourTitle = s.Tour.Title,
                s.StartAtUtc,
                s.EndAtUtc,
                s.MeetingPoint,
                OperatorUserId = s.Tour.OperatorUserId,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (schedule is null)
        {
            return await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, null, nowUtc,
                CheckInErrorCodes.ScheduleNotFound, "The departure schedule could not be found.");
        }

        if (schedule.OperatorUserId != operatorId)
        {
            return await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, null, nowUtc,
                CheckInErrorCodes.ScheduleForbidden,
                "You do not have permission to check in for this tour departure.");
        }

        // 3. Validate departure date window in Asia/Ho_Chi_Minh (BR-84, BR-108)
        var validityWindow = TicketValidityWindow.For(schedule.StartAtUtc);
        if (nowUtc < validityWindow.ValidFromUtc || nowUtc > validityWindow.ValidUntilUtc)
        {
            return await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, null, nowUtc,
                CheckInErrorCodes.DepartureNotToday,
                "Check-in is only permitted on the departure date.");
        }

        // 4. Resolve Ticket and Active QR (BR-83 cryptographic signature & timestamp, PC-01..PC-03)
        long ticketId;
        long? qrCodeIdFromPayload = null;
        TicketQrVerification? verifiedQr = null;

        if (!string.IsNullOrWhiteSpace(request.QrContent))
        {
            if (!ticketQrCodec.TryVerify(request.QrContent, out var verification) || verification is null)
            {
                return await RejectAsync(
                    scanAttemptId, operatorId, request.ScheduleId, null, nowUtc,
                    CheckInErrorCodes.InvalidQr,
                    "Invalid or forged QR code.");
            }

            ticketId = verification.TicketId;
            verifiedQr = verification;
        }
        else if (!string.IsNullOrWhiteSpace(request.BookingCode))
        {
            var code = request.BookingCode.Trim();
            var matchedTicketId = await dbContext.Tickets.AsNoTracking()
                .Where(t => t.TicketCode == code || t.Booking.BookingCode == code)
                .Select(t => (long?)t.Id)
                .SingleOrDefaultAsync(cancellationToken);

            if (matchedTicketId is null)
            {
                return await RejectAsync(
                    scanAttemptId, operatorId, request.ScheduleId, null, nowUtc,
                    CheckInErrorCodes.TicketNotFound,
                    "The ticket could not be found.");
            }

            ticketId = matchedTicketId.Value;
        }
        else
        {
            return await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, null, nowUtc,
                CheckInErrorCodes.InvalidInput,
                "Either QR code content or a booking code is required.");
        }

        // 5. Query initial ticket and booking state
        var ticketData = await dbContext.Tickets.AsNoTracking()
            .Where(t => t.Id == ticketId)
            .Select(t => new
            {
                t.Id,
                t.TicketCode,
                t.Status,
                t.CheckedInAtUtc,
                t.BookingId,
                BookingCode = t.Booking.BookingCode,
                BookingStatus = t.Booking.Status,
                BookingPaymentStatus = t.Booking.PaymentStatus,
                t.Booking.TourScheduleId,
                t.Booking.Quantity,
                t.Booking.TravelerUserId,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (ticketData is null)
        {
            return await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, null, nowUtc,
                CheckInErrorCodes.TicketNotFound,
                "The ticket could not be found.");
        }

        // 6. Validate schedule match (Normal Flow step 5, Abnormal Case 5.a2)
        if (ticketData.TourScheduleId != request.ScheduleId)
        {
            return await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, ticketId, nowUtc,
                CheckInErrorCodes.WrongSchedule,
                "The ticket is for a different departure schedule.");
        }

        // 7. Validate booking status (BR-82 verified payment, BR-86 cancellation invalidates ticket)
        if (ticketData.BookingStatus == BookingStatus.Cancelled)
        {
            return await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, ticketId, nowUtc,
                CheckInErrorCodes.BookingCancelled,
                "The booking for this ticket has been cancelled.");
        }

        if (ticketData.BookingStatus != BookingStatus.Confirmed || ticketData.BookingPaymentStatus != PaymentStatus.Paid)
        {
            return await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, ticketId, nowUtc,
                CheckInErrorCodes.BookingNotConfirmed,
                "The booking is not confirmed and paid.");
        }

        var passengerName = await ResolvePassengerNameAsync(ticketData.BookingId, ticketData.TravelerUserId, cancellationToken);

        // 8. Strict single-use ticket check under D3 / BR-85
        if (ticketData.Status == TicketStatus.Used)
        {
            var metadata = new Dictionary<string, object?>
            {
                ["checkedInAt"] = ticketData.CheckedInAtUtc,
                ["ticketCode"] = ticketData.TicketCode,
                ["bookingCode"] = ticketData.BookingCode,
                ["passengerName"] = passengerName,
            };

            var rejected = await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, ticketId, nowUtc,
                CheckInErrorCodes.AlreadyUsed,
                $"Ticket {ticketData.TicketCode} was already checked in at {ticketData.CheckedInAtUtc:O}.",
                metadata);
            if (rejected.ErrorCode == CheckInErrorCodes.AlreadyUsed)
            {
                await RecordCheckInLogAsync(ticketId, qrCodeIdFromPayload, operatorId,
                    CheckInScanResult.AlreadyUsed, nowUtc, request.Latitude, request.Longitude);
            }

            return rejected;
        }

        if (ticketData.Status != TicketStatus.Valid)
        {
            return await RejectAsync(
                scanAttemptId, operatorId, request.ScheduleId, ticketId, nowUtc,
                CheckInErrorCodes.TicketInvalid,
                $"Ticket is not valid for check-in (Status: {ticketData.Status}).");
        }

        // 9. Execute check-in mutation under serializable transaction with deadlock retry
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var checkInResult = await dbContext.ExecuteInSerializableTransactionAsync(async txToken =>
                {
                    var ticket = await dbContext.Tickets
                        .Where(t => t.Id == ticketId)
                        .SingleOrDefaultAsync(txToken);

                    if (ticket is null)
                        return new CheckInTransactionOutcome();

                    if (ticket.Status == TicketStatus.Used)
                        return new CheckInTransactionOutcome(AlreadyUsed: true, CheckedInAt: ticket.CheckedInAtUtc);

                    if (ticket.Status != TicketStatus.Valid)
                        return new CheckInTransactionOutcome();

                    TicketQrCode? activeQr;
                    if (verifiedQr is not null)
                    {
                        // Refresh revokes the old credential. Never substitute the replacement QR.
                        activeQr = await dbContext.TicketQrCodes
                            .Where(c => c.TicketId == ticketId && c.QrToken == verifiedQr.QrToken)
                            .SingleOrDefaultAsync(txToken);
                        if (activeQr is null || activeQr.Status != TicketQrCodeStatus.Active
                            || activeQr.QrToken != verifiedQr.QrToken
                            || activeQr.IssuedAtUtc.ToUnixTimeSeconds() != verifiedQr.IssuedAtUtc.ToUnixTimeSeconds())
                            return new CheckInTransactionOutcome(InvalidQr: true);

                        qrCodeIdFromPayload = activeQr.Id;
                    }
                    else
                    {
                        // Manual booking/ticket code is independently authorized and validated above.
                        activeQr = await dbContext.TicketQrCodes
                            .Where(c => c.TicketId == ticketId && c.Status == TicketQrCodeStatus.Active)
                            .SingleOrDefaultAsync(txToken);
                    }

                    ticket.CheckIn(nowUtc);
                    activeQr?.Consume(nowUtc);

                    var checkInLog = CheckInLog.Create(
                        ticket.Id,
                        activeQr?.Id,
                        operatorId,
                        CheckInScanResult.Success,
                        nowUtc,
                        request.Latitude,
                        request.Longitude);

                    dbContext.CheckInLogs.Add(checkInLog);
                    await dbContext.SaveChangesAsync(txToken);

                    // Prepare the complete response before commit: aggregation failure must roll back
                    // the ticket, QR and log along with the rest of this transaction.
                    var progress = await ComputeDepartureProgressAsync(
                        schedule.Id, schedule.TourTitle, schedule.StartAtUtc, schedule.EndAtUtc, schedule.MeetingPoint, txToken);
                    return new CheckInTransactionOutcome(Result: new CheckInResultDto(
                        ticketData.TicketCode,
                        ticketData.BookingCode,
                        passengerName,
                        ticketData.Quantity,
                        nowUtc,
                        progress));
                }, cancellationToken);

                if (checkInResult.InvalidQr)
                {
                    return await RejectAsync(
                        scanAttemptId, operatorId, request.ScheduleId, ticketId, nowUtc,
                        CheckInErrorCodes.InvalidQr, "Invalid or revoked QR code.");
                }

                if (checkInResult.AlreadyUsed)
                {
                    var metadata = new Dictionary<string, object?>
                    {
                        ["checkedInAt"] = checkInResult.CheckedInAt ?? ticketData.CheckedInAtUtc,
                        ["ticketCode"] = ticketData.TicketCode,
                        ["bookingCode"] = ticketData.BookingCode,
                        ["passengerName"] = passengerName,
                    };

                    var rejected = await RejectAsync(
                        scanAttemptId, operatorId, request.ScheduleId, ticketId, nowUtc,
                        CheckInErrorCodes.AlreadyUsed,
                        $"Ticket {ticketData.TicketCode} was already checked in.",
                        metadata);
                    if (rejected.ErrorCode == CheckInErrorCodes.AlreadyUsed)
                    {
                        await RecordCheckInLogAsync(ticketId, qrCodeIdFromPayload, operatorId,
                            CheckInScanResult.AlreadyUsed, nowUtc, request.Latitude, request.Longitude);
                    }

                    return rejected;
                }

                if (checkInResult.Result is null)
                {
                    return await RejectAsync(
                        scanAttemptId, operatorId, request.ScheduleId, ticketId, nowUtc,
                        CheckInErrorCodes.TicketInvalid,
                        "Ticket could not be checked in.");
                }

                return Result.Success(checkInResult.Result);
            }
            catch (Exception ex) when (attempt < MaxConcurrencyAttempts && dbContext.IsDeadlock(ex))
            {
                dbContext.ClearTrackedEntities();
                var delayMs = Random.Shared.Next(15, 40) * (1 << (attempt - 1));
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
        }
    }

    private sealed record CheckInTransactionOutcome(
        bool AlreadyUsed = false,
        DateTimeOffset? CheckedInAt = null,
        bool InvalidQr = false,
        CheckInResultDto? Result = null);

    private async Task<string> ResolvePassengerNameAsync(
        long bookingId,
        long travelerUserId,
        CancellationToken cancellationToken)
    {
        var primaryParticipant = await dbContext.BookingParticipants.AsNoTracking()
            .Where(p => p.BookingId == bookingId)
            .OrderBy(p => p.Id)
            .Select(p => p.FullName)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(primaryParticipant))
        {
            return primaryParticipant;
        }

        var bookerName = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == travelerUserId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(bookerName) ? "Traveler" : bookerName;
    }

    private async Task<DepartureProgressDto> ComputeDepartureProgressAsync(
        long scheduleId,
        string tourTitle,
        DateTimeOffset startAtUtc,
        DateTimeOffset endAtUtc,
        string? meetingPoint,
        CancellationToken cancellationToken)
    {
        var totalEligible = await dbContext.Bookings.AsNoTracking()
            .Where(b => b.TourScheduleId == scheduleId
                && b.Status == BookingStatus.Confirmed
                && b.PaymentStatus == PaymentStatus.Paid)
            .SumAsync(b => (int?)b.Quantity, cancellationToken) ?? 0;

        var checkedIn = await (
            from b in dbContext.Bookings.AsNoTracking()
            where b.TourScheduleId == scheduleId
                && b.Status == BookingStatus.Confirmed
                && b.PaymentStatus == PaymentStatus.Paid
            join t in dbContext.Tickets.AsNoTracking() on b.Id equals t.BookingId
            where t.Status == TicketStatus.Used
            select (int?)b.Quantity
        ).SumAsync(cancellationToken) ?? 0;

        var waiting = Math.Max(0, totalEligible - checkedIn);

        return new DepartureProgressDto(
            scheduleId,
            tourTitle,
            startAtUtc,
            endAtUtc,
            meetingPoint,
            checkedIn,
            totalEligible,
            waiting);
    }

    private async Task RecordCheckInLogAsync(
        long ticketId,
        long? qrCodeId,
        long operatorId,
        CheckInScanResult result,
        DateTimeOffset nowUtc,
        decimal? latitude,
        decimal? longitude)
    {
        try
        {
            var log = CheckInLog.Create(ticketId, qrCodeId, operatorId, result, nowUtc, latitude, longitude);
            dbContext.CheckInLogs.Add(log);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Logging failure does not disrupt response delivery.
            _logger.LogError(ex, "Failed to record check-in log: ticketId={TicketId}, operatorId={OperatorId}, result={Result}",
                ticketId, operatorId, result);
        }
    }

    private async Task<Result<CheckInResultDto>> RejectAsync(
        Guid scanAttemptId,
        long operatorId,
        long scheduleId,
        long? ticketId,
        DateTimeOffset nowUtc,
        string errorCode,
        string errorMessage,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var outcome = CheckInAuditEvent.Rejected(
            CheckInAuditEvent.DeriveRejectionEventId(scanAttemptId),
            scanAttemptId, nowUtc, operatorId, scheduleId, ticketId, errorCode);
        if (!await IsDurablyConfirmedAsync(outcome, CancellationToken.None))
        {
            return JournalUnavailable();
        }

        return Result.Failure<CheckInResultDto>(errorCode, errorMessage, metadata);
    }

    private async Task<bool> IsDurablyConfirmedAsync(
        CheckInAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await auditJournal.AppendAsync(auditEvent, cancellationToken)
                == CheckInAuditWriteResult.Confirmed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Check-in audit journal append failed: eventId={EventId}, attemptId={AttemptId}, eventType={EventType}",
                auditEvent.EventId, auditEvent.ScanAttemptId, auditEvent.EventType);
            return false;
        }
    }

    private static Result<CheckInResultDto> JournalUnavailable() =>
        Result.Failure<CheckInResultDto>(
            CheckInErrorCodes.Unavailable,
            CheckInErrorCodes.UnavailableMessage);
}
using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Bookings.Common;
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
        // 1. Verify operator access (BR-108)
        var access = await OperatorBookingAccess.ResolveOperatorIdAsync(
            dbContext, currentUserService, cancellationToken);
        if (access.IsFailure)
        {
            return Result.Failure<CheckInResultDto>(access.ErrorCode!, access.ErrorMessage!);
        }

        var operatorId = access.Value;
        var nowUtc = clock.UtcNow;

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
            return Result.Failure<CheckInResultDto>(
                CheckInErrorCodes.ScheduleNotFound, "The departure schedule could not be found.");
        }

        if (schedule.OperatorUserId != operatorId)
        {
            await RecordAuditRejectionAsync(operatorId, null, "Operator does not own this tour departure schedule.", nowUtc);
            return Result.Failure<CheckInResultDto>(
                CheckInErrorCodes.ScheduleForbidden, "You do not have permission to check in for this tour departure.");
        }

        // 3. Validate departure date window in Asia/Ho_Chi_Minh (BR-84, BR-108)
        var validityWindow = TicketValidityWindow.For(schedule.StartAtUtc);
        if (nowUtc < validityWindow.ValidFromUtc || nowUtc > validityWindow.ValidUntilUtc)
        {
            await RecordAuditRejectionAsync(operatorId, null, "Check-in attempted outside the departure date window.", nowUtc);
            return Result.Failure<CheckInResultDto>(
                CheckInErrorCodes.DepartureNotToday,
                "Check-in is only permitted on the departure date.");
        }

        // 4. Resolve Ticket and Active QR (BR-83 cryptographic signature & timestamp, PC-01..PC-03)
        long ticketId;
        long? qrCodeIdFromPayload = null;

        if (!string.IsNullOrWhiteSpace(request.QrContent))
        {
            if (!ticketQrCodec.TryVerify(request.QrContent, out var verification) || verification is null)
            {
                await RecordAuditRejectionAsync(operatorId, null, "Invalid or forged QR code cryptographic payload.", nowUtc);
                return Result.Failure<CheckInResultDto>(
                    CheckInErrorCodes.InvalidQr,
                    "Invalid or forged QR code.");
            }

            ticketId = verification.TicketId;
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
                await RecordAuditRejectionAsync(operatorId, null, "No ticket found matching the submitted booking code.", nowUtc);
                return Result.Failure<CheckInResultDto>(
                    CheckInErrorCodes.TicketNotFound,
                    "The ticket could not be found.");
            }

            ticketId = matchedTicketId.Value;
        }
        else
        {
            return Result.Failure<CheckInResultDto>(
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
            await RecordAuditRejectionAsync(operatorId, null, "Ticket not found.", nowUtc);
            return Result.Failure<CheckInResultDto>(
                CheckInErrorCodes.TicketNotFound,
                "The ticket could not be found.");
        }

        // 6. Validate schedule match (Normal Flow step 5, Abnormal Case 5.a2)
        if (ticketData.TourScheduleId != request.ScheduleId)
        {
            await RecordAuditRejectionAsync(operatorId, ticketId, "Ticket is for a different departure schedule.", nowUtc);
            return Result.Failure<CheckInResultDto>(
                CheckInErrorCodes.WrongSchedule,
                "The ticket is for a different departure schedule.");
        }

        // 7. Validate booking status (BR-82 verified payment, BR-86 cancellation invalidates ticket)
        if (ticketData.BookingStatus == BookingStatus.Cancelled)
        {
            await RecordAuditRejectionAsync(operatorId, ticketId, "Booking was cancelled.", nowUtc);
            return Result.Failure<CheckInResultDto>(
                CheckInErrorCodes.BookingCancelled,
                "The booking for this ticket has been cancelled.");
        }

        if (ticketData.BookingStatus != BookingStatus.Confirmed || ticketData.BookingPaymentStatus != PaymentStatus.Paid)
        {
            await RecordAuditRejectionAsync(operatorId, ticketId, "Booking is not confirmed or paid.", nowUtc);
            return Result.Failure<CheckInResultDto>(
                CheckInErrorCodes.BookingNotConfirmed,
                "The booking is not confirmed and paid.");
        }

        var passengerName = await ResolvePassengerNameAsync(ticketData.BookingId, ticketData.TravelerUserId, cancellationToken);

        // 8. Strict single-use ticket check under D3 / BR-85
        if (ticketData.Status == TicketStatus.Used)
        {
            await RecordCheckInLogAsync(ticketId, qrCodeIdFromPayload, operatorId, CheckInScanResult.AlreadyUsed, nowUtc, request.Latitude, request.Longitude);

            var metadata = new Dictionary<string, object?>
            {
                ["checkedInAt"] = ticketData.CheckedInAtUtc,
                ["ticketCode"] = ticketData.TicketCode,
                ["bookingCode"] = ticketData.BookingCode,
                ["passengerName"] = passengerName,
            };

            return Result.Failure<CheckInResultDto>(
                CheckInErrorCodes.AlreadyUsed,
                $"Ticket {ticketData.TicketCode} was already checked in at {ticketData.CheckedInAtUtc:O}.",
                metadata);
        }

        if (ticketData.Status != TicketStatus.Valid)
        {
            await RecordAuditRejectionAsync(operatorId, ticketId, $"Ticket status is {ticketData.Status}.", nowUtc);
            return Result.Failure<CheckInResultDto>(
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
                        return (Success: false, AlreadyUsed: false, CheckedInAt: (DateTimeOffset?)null);

                    if (ticket.Status == TicketStatus.Used)
                        return (Success: false, AlreadyUsed: true, CheckedInAt: ticket.CheckedInAtUtc);

                    if (ticket.Status != TicketStatus.Valid)
                        return (Success: false, AlreadyUsed: false, CheckedInAt: (DateTimeOffset?)null);

                    ticket.CheckIn(nowUtc);

                    var activeQr = await dbContext.TicketQrCodes
                        .Where(c => c.TicketId == ticketId && c.Status == TicketQrCodeStatus.Active)
                        .SingleOrDefaultAsync(txToken);

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

                    return (Success: true, AlreadyUsed: false, CheckedInAt: (DateTimeOffset?)nowUtc);
                }, cancellationToken);

                if (checkInResult.AlreadyUsed)
                {
                    await RecordCheckInLogAsync(ticketId, qrCodeIdFromPayload, operatorId, CheckInScanResult.AlreadyUsed, nowUtc, request.Latitude, request.Longitude);

                    var metadata = new Dictionary<string, object?>
                    {
                        ["checkedInAt"] = checkInResult.CheckedInAt ?? ticketData.CheckedInAtUtc,
                        ["ticketCode"] = ticketData.TicketCode,
                        ["bookingCode"] = ticketData.BookingCode,
                        ["passengerName"] = passengerName,
                    };

                    return Result.Failure<CheckInResultDto>(
                        CheckInErrorCodes.AlreadyUsed,
                        $"Ticket {ticketData.TicketCode} was already checked in.",
                        metadata);
                }

                if (!checkInResult.Success)
                {
                    return Result.Failure<CheckInResultDto>(
                        CheckInErrorCodes.TicketInvalid,
                        "Ticket could not be checked in.");
                }

                break;
            }
            catch (Exception ex) when (attempt < MaxConcurrencyAttempts && dbContext.IsDeadlock(ex))
            {
                dbContext.ClearTrackedEntities();
                var delayMs = Random.Shared.Next(15, 40) * (1 << (attempt - 1));
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
        }

        // 10. Dynamic departure aggregation (D1)
        var progress = await ComputeDepartureProgressAsync(
            schedule.Id, schedule.TourTitle, schedule.StartAtUtc, schedule.EndAtUtc, schedule.MeetingPoint, cancellationToken);

        return Result.Success(new CheckInResultDto(
            ticketData.TicketCode,
            ticketData.BookingCode,
            passengerName,
            ticketData.Quantity,
            nowUtc,
            progress));
    }

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

    private async Task RecordAuditRejectionAsync(
        long operatorId,
        long? ticketId,
        string reason,
        DateTimeOffset nowUtc)
    {
        try
        {
            var audit = AuditLog.CreateRecordedOutcome(
                operatorId,
                AuditActionTypes.CheckInScanRejected,
                AuditEntityTypes.CheckIn,
                ticketId,
                nowUtc,
                AuditOutcome.Failure,
                reason: reason);
            dbContext.AuditLogs.Add(audit);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Database-outage limitation: rejection audit log failure is suppressed from disrupting response,
            // but logged for structured diagnostic observability.
            _logger.LogError(ex, "Failed to persist rejection audit log: operatorId={OperatorId}, ticketId={TicketId}, reason={Reason}",
                operatorId, ticketId, reason);
        }
    }
}
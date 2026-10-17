using System.Security.Cryptography;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Expiry;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Create;

public sealed class CreateTourBookingCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    PendingBookingExpiryProcessor expiryProcessor,
    TourBookingOptions options,
    IPiiProtector piiProtector)
    : IRequestHandler<CreateTourBookingCommand, Result<CreateTourBookingResponse>>
{
    // Mirrors CreateTourPackageCommandHandler: concurrent duplicates all read "no operation yet"
    // under SERIALIZABLE and then collide on the (traveler, key) unique index, or the booking_code
    // unique index, or are chosen as deadlock victims. Re-running lets the loser find the winner's
    // committed operation and replay it instead of failing.
    private const int MaxAttempts = 5;

    public async Task<Result<CreateTourBookingResponse>> Handle(
        CreateTourBookingCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
            return AccessDenied();
        if (request.IdempotencyKey == Guid.Empty)
            return Result.Failure<CreateTourBookingResponse>(
                TourBookingErrorCodes.IdempotencyKeyRequired, "An Idempotency-Key is required to create a booking.");
        if (request.Participants is null || request.Participants.Count == 0)
            return Result.Failure<CreateTourBookingResponse>(
                TourBookingErrorCodes.ParticipantsRequired, "At least one participant is required.");
        if (!long.TryParse(request.TourScheduleId, out var scheduleId) || scheduleId <= 0)
            return Result.Failure<CreateTourBookingResponse>(
                TourBookingErrorCodes.ScheduleNotFound, "The selected tour departure was not found.");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CreateAttemptAsync(request, travelerId, scheduleId, cancellationToken);
            }
            catch (Exception exception) when (attempt < MaxAttempts
                && (exception is DbUpdateException || dbContext.IsDeadlock(exception)))
            {
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<Result<CreateTourBookingResponse>> CreateAttemptAsync(
        CreateTourBookingCommand request, long travelerId, long scheduleId, CancellationToken cancellationToken)
    {
        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            var isActiveTraveler = await dbContext.Users.AsNoTracking().AnyAsync(user =>
                user.Id == travelerId && user.Role == UserRole.Traveler && user.Status == AccountStatus.Active,
                transactionCancellationToken);
            if (!isActiveTraveler) return AccessDenied();

            var now = clock.UtcNow;
            await expiryProcessor.ExpireDueBookingsForTravelerInCurrentTransactionAsync(
                travelerId, transactionCancellationToken);
            var fingerprint = ComputeFingerprint(request);
            var existingOperation = await dbContext.TourBookingCreateOperations
                .Include(operation => operation.Booking)
                .SingleOrDefaultAsync(operation =>
                    operation.TravelerUserId == travelerId && operation.IdempotencyKey == request.IdempotencyKey,
                    transactionCancellationToken);
            if (existingOperation is not null)
                return ReplayExisting(existingOperation, fingerprint);

            var schedule = await dbContext.TourSchedules
                .Include(scheduleEntity => scheduleEntity.Tour)
                .SingleOrDefaultAsync(scheduleEntity => scheduleEntity.Id == scheduleId, transactionCancellationToken);
            if (schedule is null || !IsPubliclyBookableTour(schedule.Tour, now))
                return Result.Failure<CreateTourBookingResponse>(
                    TourBookingErrorCodes.ScheduleNotFound, "The selected tour departure was not found.");
            if (!schedule.IsOpenForBooking(now))
                return Result.Failure<CreateTourBookingResponse>(
                    TourBookingErrorCodes.ScheduleNotOpen, "This tour departure is no longer open for booking.");

            var unpaidCount = await dbContext.Bookings.AsNoTracking().CountAsync(booking =>
                booking.TravelerUserId == travelerId && booking.Status == BookingStatus.PendingPayment,
                transactionCancellationToken);
            if (unpaidCount >= options.MaxUnpaidBookingsPerTraveler)
                return Result.Failure<CreateTourBookingResponse>(
                    TourBookingErrorCodes.TooManyUnpaidBookings,
                    "You have too many pending bookings; complete payment or cancel one to proceed.");

            if (request.Participants.Count > schedule.RemainingCapacity)
                return Result.Failure<CreateTourBookingResponse>(
                    TourBookingErrorCodes.InsufficientCapacity,
                    $"Only {schedule.RemainingCapacity} slot(s) remain for this departure.");

            Voucher? voucher = null;
            if (!string.IsNullOrWhiteSpace(request.VoucherCode))
            {
                var normalizedCode = request.VoucherCode.Trim().ToUpperInvariant();
                voucher = await dbContext.Vouchers.Include(v => v.ApplicableTours)
                    .SingleOrDefaultAsync(v => v.Code == normalizedCode, transactionCancellationToken);
                if (voucher is null)
                    return Result.Failure<CreateTourBookingResponse>(
                        TourBookingErrorCodes.VoucherNotFound, "The voucher code was not found.");
            }

            var redemptionCount = voucher is null
                ? 0
                : await dbContext.VoucherRedemptions.AsNoTracking().Include(r => r.Booking)
                    .CountAsync(r => r.VoucherId == voucher.Id && r.Booking.TravelerUserId == travelerId,
                        transactionCancellationToken);

            var participantTypes = request.Participants.Select(participant => participant.ParticipantType).ToList();
            var pricing = TourBookingPricingCalculator.Calculate(
                schedule, schedule.Tour.BasePrice, participantTypes, voucher, schedule.TourId, redemptionCount, now);
            if (pricing.IsFailure)
                return Result.Failure<CreateTourBookingResponse>(pricing.ErrorCode!, pricing.ErrorMessage!);

            var todayStartUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var dailySequence = await dbContext.Bookings.AsNoTracking().CountAsync(booking =>
                booking.BookedAtUtc >= todayStartUtc, transactionCancellationToken) + 1;
            var bookingCode = Booking.GenerateCode(now, dailySequence);

            // BR-33: the document number is PII and must be encrypted before it reaches the entity;
            // nothing downstream of this point ever sees or persists the plaintext.
            var participants = request.Participants.Select(participant => BookingParticipant.Create(
                participant.FullName, participant.ParticipantType, participant.PhoneNumber, participant.Email,
                participant.IdDocumentType,
                string.IsNullOrWhiteSpace(participant.IdDocumentNumber)
                    ? null
                    : piiProtector.Protect(participant.IdDocumentNumber),
                participant.Nationality)).ToList();

            var effectiveAdultPrice = schedule.GetEffectivePrice(ParticipantType.Adult, schedule.Tour.BasePrice);
            var booking = Booking.Create(bookingCode, travelerId, scheduleId, voucher?.Id, effectiveAdultPrice,
                pricing.Value.DiscountAmount, pricing.Value.TotalAmount, participants, now,
                now.Add(options.PaymentExpiry));

            schedule.ReserveCapacity(participants.Count);
            dbContext.Bookings.Add(booking);
            if (voucher is not null && pricing.Value.DiscountAmount > 0)
            {
                voucher.IncrementUsage();
                dbContext.VoucherRedemptions.Add(VoucherRedemption.Create(voucher, booking, pricing.Value.DiscountAmount, now));
            }

            dbContext.TourBookingCreateOperations.Add(
                TourBookingCreateOperation.Create(travelerId, request.IdempotencyKey, fingerprint, booking, now));
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(ToResponse(booking, isReplay: false));
        }, cancellationToken);
    }

    private static Result<CreateTourBookingResponse> ReplayExisting(
        TourBookingCreateOperation existingOperation, string fingerprint)
    {
        if (!string.Equals(existingOperation.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
            return Result.Failure<CreateTourBookingResponse>(
                TourBookingErrorCodes.IdempotencyKeyPayloadMismatch,
                "The Idempotency-Key was already used with different request data.");

        if (existingOperation.Booking is null)
            return Result.Failure<CreateTourBookingResponse>(
                TourBookingErrorCodes.IdempotencyKeyPayloadMismatch, "The previous create request did not complete.");

        return Result.Success(ToResponse(existingOperation.Booking, isReplay: true));
    }

    private static CreateTourBookingResponse ToResponse(Booking booking, bool isReplay) => new(
        booking.Id.ToString(),
        booking.BookingCode,
        booking.Status.ToString(),
        booking.TourScheduleId.ToString(),
        "VND",
        booking.SubtotalAmount,
        booking.DiscountAmount,
        booking.TotalAmount,
        booking.PaymentExpiresAtUtc ?? booking.BookedAtUtc,
        booking.BookedAtUtc)
    { IsReplay = isReplay };

    private static bool IsPubliclyBookableTour(Tour tour, DateTimeOffset now) =>
        tour.Status == TourStatus.Published ||
        (tour.Status == TourStatus.Approved && tour.PublishedAtUtc.HasValue && tour.PublishedAtUtc <= now);

    private static string ComputeFingerprint(CreateTourBookingCommand request)
    {
        byte[] canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.TourScheduleId,
            Participants = request.Participants.Select(participant => new
            {
                FullName = participant.FullName.Trim(),
                participant.ParticipantType,
                PhoneNumber = participant.PhoneNumber?.Trim(),
                Email = participant.Email?.Trim(),
                participant.IdDocumentType,
                IdDocumentNumber = participant.IdDocumentNumber?.Trim(),
                Nationality = participant.Nationality?.Trim().ToUpperInvariant(),
            }),
            VoucherCode = request.VoucherCode?.Trim().ToUpperInvariant(),
        });
        return Convert.ToHexString(SHA256.HashData(canonicalPayload));
    }

    private static Result<CreateTourBookingResponse> AccessDenied() =>
        Result.Failure<CreateTourBookingResponse>(
            TourBookingErrorCodes.AccessRequired, "An active Traveler account is required.");
}

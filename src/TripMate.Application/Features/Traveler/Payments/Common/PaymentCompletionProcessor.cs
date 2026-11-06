using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Common;

public sealed class PaymentCompletionProcessor(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock) : IPaymentCompletionProcessor
{
    private const int MaxAttempts = 3;

    public async Task<Result<PaymentCompletionResult>> CompleteAsync(
        long? transactionId,
        long? orderCode,
        PaymentTransactionStatus outcome,
        string? gatewayRef,
        string? rawResponse,
        decimal? verifiedAmount,
        string? verifiedCurrency,
        CancellationToken cancellationToken)
    {
        // Bounded retry coordinator: only a concurrent winner of the unique
        // Itineraries.source_booking_id index (or a deadlock victim) may be re-run; every
        // other persistence failure propagates untouched. The final attempt is never caught.
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(
                    token => CompleteOnceAsync(transactionId, orderCode, outcome, gatewayRef,
                        rawResponse, verifiedAmount, verifiedCurrency, token),
                    cancellationToken);
            }
            catch (DbUpdateException exception) when (attempt < MaxAttempts
                && (dbContext.IsSourceBookingUniqueConflict(exception)
                    || dbContext.IsDeadlock(exception)))
            {
                dbContext.ClearTrackedEntities();
            }
            catch (Exception exception) when (attempt < MaxAttempts
                && dbContext.IsDeadlock(exception))
            {
                // Reads can surface SQL Server error 1205 directly instead of wrapping it in
                // DbUpdateException. Retry those deadlocks through the same bounded loop.
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<Result<PaymentCompletionResult>> CompleteOnceAsync(
        long? transactionId,
        long? orderCode,
        PaymentTransactionStatus outcome,
        string? gatewayRef,
        string? rawResponse,
        decimal? verifiedAmount,
        string? verifiedCurrency,
        CancellationToken cancellationToken)
    {
        if (transactionId is not > 0 && orderCode is not > 0)
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentNotFound, "Payment transaction identification is required.");
        }

        PaymentTransaction? transaction = null;
        if (transactionId is > 0)
        {
            transaction = await dbContext.PaymentTransactions
                .Include(t => t.Booking)
                .FirstOrDefaultAsync(t => t.Id == transactionId.Value, cancellationToken);
        }
        else if (orderCode is > 0)
        {
            transaction = await dbContext.PaymentTransactions
                .Include(t => t.Booking)
                .FirstOrDefaultAsync(t => t.OrderCode == orderCode.Value, cancellationToken);
        }

        if (transaction is null)
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentNotFound, "Payment transaction was not found.");
        }

        var booking = transaction.Booking
            ?? await dbContext.Bookings.FirstOrDefaultAsync(b => b.Id == transaction.BookingId, cancellationToken);

        if (booking is null)
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentNotFound, "Booking associated with transaction was not found.");
        }

        if (verifiedAmount.HasValue && verifiedAmount.Value != transaction.Amount)
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Gateway amount does not match transaction amount.");
        }

        if (!string.IsNullOrWhiteSpace(verifiedCurrency) &&
            !string.Equals(verifiedCurrency.Trim(), transaction.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Gateway currency does not match transaction currency.");
        }

        var now = clock.UtcNow;
        var statusChanged = false;
        var acceptedPayment = false;

        switch (outcome)
        {
            case PaymentTransactionStatus.Success:
                if (transaction.Status == PaymentTransactionStatus.Pending)
                {
                    var commissionRate = await dbContext.TourSchedules.Where(s => s.Id == booking.TourScheduleId).Select(s => dbContext.OperatorProfiles.Where(o => o.UserId == s.Tour.OperatorUserId).Select(o => o.CommissionRate).FirstOrDefault()).FirstOrDefaultAsync(cancellationToken);
                    transaction.MarkSuccess(gatewayRef, rawResponse, now, commissionRate);
                    statusChanged = true;
                    acceptedPayment = true;
                }
                else if (transaction.Status != PaymentTransactionStatus.Success)
                {
                    // A terminal transaction must never be reopened by a late PayOS webhook.
                    // Retain its gateway evidence for reconciliation/refund processing instead.
                    transaction.RecordLateGatewayNotification(gatewayRef, rawResponse, now);
                }

                // Decision 6 & 8: If booking is still eligible (PendingPayment and not expired), confirm it.
                // If booking has already expired, keep booking Expired and do not reinstate.
                if (acceptedPayment && booking.Status == BookingStatus.PendingPayment)
                {
                    if (booking.PaymentExpiresAtUtc.HasValue && booking.PaymentExpiresAtUtc.Value < now)
                    {
                        await BookingExpiryEffects.ExpireAsync(dbContext, booking, now, cancellationToken);
                    }
                    else
                    {
                        // UC-29: only the transition to Confirmed issues the ticket, so repeated or late
                        // notifications never create a second one (UC-28 10.a2). The QR is issued on first view.
                        if (booking.MarkPaid(now))
                            booking.IssueTicket(now);
                        statusChanged = true;
                        await MaterializeBookedTourItineraryAsync(
                            booking, now, cancellationToken);
                    }
                }
                break;

            case PaymentTransactionStatus.Cancelled:
                if (transaction.Status == PaymentTransactionStatus.Pending)
                {
                    transaction.MarkCancelled(rawResponse, now);
                    statusChanged = true;
                }
                // Decision 2: Booking remains PendingPayment until its normal expiry
                break;

            case PaymentTransactionStatus.Expired:
                if (transaction.Status == PaymentTransactionStatus.Pending)
                {
                    transaction.MarkExpired(rawResponse, now);
                    statusChanged = true;
                }
                // Check if booking hold has expired too
                if (booking.Status == BookingStatus.PendingPayment &&
                    booking.PaymentExpiresAtUtc.HasValue &&
                    booking.PaymentExpiresAtUtc.Value <= now)
                {
                    await BookingExpiryEffects.ExpireAsync(dbContext, booking, now, cancellationToken);
                    statusChanged = true;
                }
                break;

            case PaymentTransactionStatus.Failed:
                if (transaction.Status == PaymentTransactionStatus.Pending)
                {
                    transaction.MarkFailed(rawResponse, now);
                    statusChanged = true;
                }
                break;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new PaymentCompletionResult(transaction, booking, statusChanged));
    }

    private async Task MaterializeBookedTourItineraryAsync(
        Booking booking, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // UC-70 exact commercial session identity: the paid booking gets one immutable
        // BookedTour itinerary snapshot. Gateway replays skip the MarkPaid branch and the
        // SourceBookingId guard keeps concurrent confirmations idempotent; the unique
        // filtered index IX_Itineraries_SourceBookingId is the final race protection.
        bool itineraryExists = await dbContext.Itineraries.AnyAsync(
            candidate => candidate.SourceBookingId == booking.Id, cancellationToken);
        if (itineraryExists)
        {
            return;
        }

        var departure = await dbContext.TourSchedules
            .Include(scheduleEntity => scheduleEntity.Tour)
            .ThenInclude(tour => tour.ItineraryItems)
            .FirstOrDefaultAsync(scheduleEntity => scheduleEntity.Id == booking.TourScheduleId,
                cancellationToken);
        if (departure?.Tour is null)
        {
            AuditMaterializationFailure(booking, BookedTourItineraryMaterializer.DepartureNotFoundFailureCode, now);
            return;
        }

        BookedTourItineraryMaterializer.MaterializationResult materialized =
            BookedTourItineraryMaterializer.Materialize(departure.Tour, departure, booking, now);
        if (materialized.Itinerary is null)
        {
            // Payment truth survives: the gateway acknowledgement and the confirmed booking
            // are still committed; the operational failure is recorded for recovery instead
            // of persisting a misleading empty Active itinerary or inventing times.
            AuditMaterializationFailure(booking, materialized.FailureCode!, now);
            return;
        }

        dbContext.Itineraries.Add(materialized.Itinerary);
    }

    private void AuditMaterializationFailure(Booking booking, string failureCode, DateTimeOffset now)
    {
        // System workflow outcome: no traveler or operator performed the failed step, so the
        // audit actor stays null (the repository's convention for system records).
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            actorUserId: null,
            AuditActionTypes.BookingItineraryMaterializationFailed,
            AuditEntityTypes.Booking, booking.Id, now, AuditOutcome.Failure,
            $"Booked-tour itinerary materialization failed: {failureCode}."));
    }
}
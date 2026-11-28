using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Application.Features.Traveler.RefundAccount;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Create;

/// <summary>
/// Shared helper for creating DirectRefundCase obligations inside an open transaction.
/// Called by the three handoff paths: cancellation, emergency, and accepted complaint.
/// Enforces the concurrent-cap rule (spec §6): refundAmount ≤ paidAmount − completed − reserved.
/// </summary>
public static class DirectRefundCaseCreator
{
    public const string ErrorCapExceeded = "direct_refund.cap_exceeded";
    public const string ErrorDuplicateSource = "direct_refund.duplicate_source";

    /// <summary>
    /// Validates the concurrent cap and one-obligation-per-source invariant, then adds the entity
    /// to the context. Caller must call SaveChangesAsync to persist.
    /// </summary>
    public static async Task<Result<DirectRefundCase>> CreateAsync(
        IApplicationDbContext dbContext,
        long bookingId,
        string sourceType,
        long refundAmountVnd,
        long paidAmountVnd,
        DateTimeOffset nowUtc,
        long? sourceRefId,
        CancellationToken cancellationToken,
        IPiiProtector? piiProtector = null)
    {
        // One active obligation per source type per booking (Disputed cases are closed).
        bool exists = await dbContext.DirectRefundCases.AsNoTracking()
            .AnyAsync(c => c.BookingId == bookingId
                && c.SourceType == sourceType
                && c.Status != DirectRefundCase.StatusDisputed, cancellationToken);
        if (exists)
            return Result.Failure<DirectRefundCase>(ErrorDuplicateSource,
                $"An active direct refund obligation for source '{sourceType}' already exists for booking {bookingId}.");

        // Concurrent cap: refundAmount ≤ paidAmount − completedRefunds − reservedRefunds.
        long completed = await dbContext.DirectRefundCases.AsNoTracking()
            .Where(c => c.BookingId == bookingId && c.Status == DirectRefundCase.StatusCompleted)
            .SumAsync(c => (long?)c.RefundAmountVnd, cancellationToken) ?? 0L;
        long reserved = await dbContext.DirectRefundCases.AsNoTracking()
            .Where(c => c.BookingId == bookingId
                && c.Status != DirectRefundCase.StatusCompleted
                && c.Status != DirectRefundCase.StatusDisputed)
            .SumAsync(c => (long?)c.RefundAmountVnd, cancellationToken) ?? 0L;
        long available = paidAmountVnd - completed - reserved;
        if (refundAmountVnd > available)
            return Result.Failure<DirectRefundCase>(ErrorCapExceeded,
                $"Refund amount {refundAmountVnd} VND exceeds available cap {available} VND for booking {bookingId}.");

        var directCase = DirectRefundCase.Create(bookingId, sourceType, refundAmountVnd, paidAmountVnd, nowUtc, sourceRefId);
        var travelerId = await dbContext.Bookings.AsNoTracking().Where(booking => booking.Id == bookingId)
            .Select(booking => booking.TravelerUserId).SingleOrDefaultAsync(cancellationToken);
        var saved = travelerId > 0 ? await dbContext.TravelerRefundAccounts.AsNoTracking()
            .SingleOrDefaultAsync(account => account.Id == travelerId, cancellationToken) : null;
        if (saved is not null && piiProtector is not null)
        {
            try
            {
                var recipient = TravelerRefundAccountStore.Read(saved, piiProtector);
                directCase.ConfirmRecipient(recipient.BankCode, recipient.AccountNumber, recipient.AccountHolder, nowUtc);
            }
            catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException)
            {
                // Preserve the refund obligation; an unreadable default must never guess a recipient
                // or prevent an accepted complaint from creating its refund case.
                // AwaitingRecipient retains the existing explicit manual confirmation flow.
            }
        }
        dbContext.DirectRefundCases.Add(directCase);
        return Result.Success(directCase);
    }
}
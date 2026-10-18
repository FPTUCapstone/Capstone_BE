using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Cancel;

/// <summary>
/// Records that the traveler left the PayOS checkout through its cancel URL.
/// A cancellation ends only that payment attempt; the booking remains held until
/// its normal payment expiry so the traveler can create a replacement link.
/// </summary>
public record CancelPaymentLinkCommand(long BookingId) : IRequest<Result<bool>>;

public sealed class CancelPaymentLinkCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IPaymentCompletionProcessor completionProcessor)
    : IRequestHandler<CancelPaymentLinkCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(
        CancelPaymentLinkCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
        {
            return Result.Failure<bool>(PaymentErrorCodes.AccessDenied, "Traveler authorization is required.");
        }

        var booking = await dbContext.Bookings
            .FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken);

        if (booking is null || booking.TravelerUserId != travelerId)
        {
            return Result.Failure<bool>(PaymentErrorCodes.PaymentNotFound, "The requested booking was not found.");
        }

        var pendingTransaction = await dbContext.PaymentTransactions
            .Where(t => t.BookingId == booking.Id && t.Status == PaymentTransactionStatus.Pending)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // Idempotent: a retry after the cancellation has been recorded is harmless.
        if (pendingTransaction is null)
        {
            return Result.Success(false);
        }

        var completed = await completionProcessor.CompleteAsync(
            transactionId: pendingTransaction.Id,
            orderCode: null,
            outcome: PaymentTransactionStatus.Cancelled,
            gatewayRef: null,
            rawResponse: "Traveler returned from the PayOS cancellation URL.",
            verifiedAmount: null,
            verifiedCurrency: null,
            cancellationToken: cancellationToken);

        return completed.IsSuccess
            ? Result.Success(completed.Value.StatusChanged)
            : Result.Failure<bool>(
                completed.ErrorCode ?? PaymentErrorCodes.PaymentCallbackInvalid,
                completed.ErrorMessage ?? "Payment cancellation could not be recorded.");
    }
}

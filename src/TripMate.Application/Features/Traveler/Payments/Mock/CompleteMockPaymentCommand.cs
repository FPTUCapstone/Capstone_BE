using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Mock;

public record MockPaymentOutcomeResponse(
    long PaymentId,
    long BookingId,
    string Status,
    string BookingStatus,
    string PaymentStatus);

public record CompleteMockPaymentCommand(
    long PaymentId,
    string Outcome) : IRequest<Result<MockPaymentOutcomeResponse>>;

public sealed class CompleteMockPaymentCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IPaymentCompletionProcessor completionProcessor,
    PaymentOptions paymentOptions)
    : IRequestHandler<CompleteMockPaymentCommand, Result<MockPaymentOutcomeResponse>>
{
    public async Task<Result<MockPaymentOutcomeResponse>> Handle(
        CompleteMockPaymentCommand request,
        CancellationToken cancellationToken)
    {
        // Decision 7: Mock completion is enabled only when Payment:Provider=Mock
        if (!string.Equals(paymentOptions.Provider, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<MockPaymentOutcomeResponse>(
                PaymentErrorCodes.PaymentMockDisabled, "Mock payment completion is disabled in this environment.");
        }

        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
        {
            return Result.Failure<MockPaymentOutcomeResponse>(
                PaymentErrorCodes.AccessDenied, "Traveler authorization is required.");
        }

        if (request.PaymentId <= 0)
        {
            return Result.Failure<MockPaymentOutcomeResponse>(
                PaymentErrorCodes.PaymentNotFound, "Payment transaction was not found.");
        }

        var transaction = await dbContext.PaymentTransactions
            .Include(t => t.Booking)
            .FirstOrDefaultAsync(t => t.Id == request.PaymentId, cancellationToken);

        if (transaction is null || transaction.Booking?.TravelerUserId != travelerId)
        {
            return Result.Failure<MockPaymentOutcomeResponse>(
                PaymentErrorCodes.PaymentNotFound, "Payment transaction was not found.");
        }

        if (!Enum.TryParse<PaymentTransactionStatus>(request.Outcome?.Trim(), ignoreCase: true, out var outcome) ||
            outcome is not (PaymentTransactionStatus.Success or PaymentTransactionStatus.Cancelled or PaymentTransactionStatus.Expired))
        {
            return Result.Failure<MockPaymentOutcomeResponse>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Invalid mock outcome. Allowed values: Success, Cancelled, Expired.");
        }

        var completionResult = await completionProcessor.CompleteAsync(
            transactionId: transaction.Id,
            orderCode: null,
            outcome: outcome,
            gatewayRef: $"MOCK-TX-{transaction.OrderCode}",
            rawResponse: $"{{\"mockOutcome\":\"{outcome}\"}}",
            verifiedAmount: null,
            verifiedCurrency: null,
            cancellationToken: cancellationToken);

        if (!completionResult.IsSuccess)
        {
            return Result.Failure<MockPaymentOutcomeResponse>(
                completionResult.ErrorCode ?? PaymentErrorCodes.PaymentCallbackInvalid,
                completionResult.ErrorMessage ?? "Payment completion failed.");
        }

        return Result.Success(new MockPaymentOutcomeResponse(
            PaymentId: completionResult.Value.Transaction.Id,
            BookingId: completionResult.Value.Booking.Id,
            Status: completionResult.Value.Transaction.Status.ToString(),
            BookingStatus: completionResult.Value.Booking.Status.ToString(),
            PaymentStatus: completionResult.Value.Booking.PaymentStatus.ToString()));
    }
}
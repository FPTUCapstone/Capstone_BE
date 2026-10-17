using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Common;

public record PaymentCompletionResult(
    PaymentTransaction Transaction,
    Booking Booking,
    bool StatusChanged);

public interface IPaymentCompletionProcessor
{
    Task<Result<PaymentCompletionResult>> CompleteAsync(
        long? transactionId,
        long? orderCode,
        PaymentTransactionStatus outcome,
        string? gatewayRef,
        string? rawResponse,
        decimal? verifiedAmount,
        string? verifiedCurrency,
        CancellationToken cancellationToken);
}

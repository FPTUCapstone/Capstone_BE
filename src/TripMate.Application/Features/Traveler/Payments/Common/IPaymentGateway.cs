using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Common;

public record PaymentLinkRequest(
    long BookingId,
    long OrderCode,
    decimal Amount,
    string Currency,
    string Description,
    DateTimeOffset? ExpiresAtUtc,
    string? ReturnUrl = null,
    string? CancelUrl = null);

public record PaymentLinkResult(
    string PaymentLinkId,
    string CheckoutUrl,
    DateTimeOffset? ExpiresAtUtc,
    string? QrCode = null);

public record PaymentLinkCancellationRequest(
    long OrderCode,
    string? PaymentLinkId,
    string Reason);

public record RefundSubmissionRequest(
    long RefundId,
    long BookingId,
    decimal Amount,
    string Currency,
    string? OriginalGatewayTransactionReference,
    string? Reason,
    string IdempotencyKey);

public record RefundSubmissionResult(
    bool IsAccepted,
    string? GatewayReference = null,
    string? FailureCode = null,
    string? FailureMessage = null,
    bool IsRetryable = true,
    bool RequiresManualReview = false);

/// <summary>
/// Resolves the gateway which accepted the original payment. Refunds must never be
/// sent through whichever provider happens to be configured for new checkouts today.
/// </summary>
public interface IRefundPaymentGatewayResolver
{
    IPaymentGateway? Resolve(PaymentGateway gateway);
}

public interface IPaymentGateway
{
    PaymentGateway Gateway { get; }

    Task<PaymentLinkResult> CreatePaymentLinkAsync(
        PaymentLinkRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invalidates a gateway payment link. Gateways without a remote link (for
    /// example Mock) may intentionally complete this operation without work.
    /// </summary>
    Task CancelPaymentLinkAsync(
        PaymentLinkCancellationRequest request,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Submits a server-owned refund to the channel that took the original payment.</summary>
    Task<RefundSubmissionResult> SubmitRefundAsync(
        RefundSubmissionRequest request,
        CancellationToken cancellationToken) => Task.FromResult(new RefundSubmissionResult(
            false, FailureCode: "payment.refund.unsupported",
            FailureMessage: "This payment gateway does not support automatic refunds.", IsRetryable: false));
}
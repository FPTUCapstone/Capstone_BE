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
}
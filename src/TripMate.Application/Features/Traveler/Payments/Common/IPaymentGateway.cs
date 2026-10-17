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
    DateTimeOffset? ExpiresAtUtc);

public interface IPaymentGateway
{
    PaymentGateway Gateway { get; }

    Task<PaymentLinkResult> CreatePaymentLinkAsync(
        PaymentLinkRequest request,
        CancellationToken cancellationToken);
}

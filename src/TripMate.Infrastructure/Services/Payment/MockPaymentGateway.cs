using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services.Payment;

public sealed class MockPaymentGateway(PaymentOptions options) : IPaymentGateway
{
    public PaymentGateway Gateway => PaymentGateway.Mock;

    public Task<PaymentLinkResult> CreatePaymentLinkAsync(
        PaymentLinkRequest request,
        CancellationToken cancellationToken)
    {
        var baseUrl = options.Mock.CheckoutBaseUrl.TrimEnd('/');
        var checkoutUrl = $"{baseUrl}?bookingId={request.BookingId}&orderCode={request.OrderCode}&amount={request.Amount:F0}";

        var expiry = request.ExpiresAtUtc
            ?? DateTimeOffset.UtcNow.AddMinutes(options.Mock.ExpiryMinutes > 0 ? options.Mock.ExpiryMinutes : 15);

        return Task.FromResult(new PaymentLinkResult(
            PaymentLinkId: $"MOCK-LINK-{request.OrderCode}",
            CheckoutUrl: checkoutUrl,
            ExpiresAtUtc: expiry));
    }
}

using TripMate.Application.Features.Traveler.Payments.Common;

namespace TripMate.Infrastructure.Services.Payment;

public sealed class OperatorPaymentGatewayFactory(IHttpClientFactory httpClientFactory, PaymentOptions options, MockPaymentGateway mockGateway)
    : IOperatorPaymentGatewayFactory
{
    private const string ClientName = "payos-operator";

    public IPaymentGateway Create(OperatorChannelSnapshot snapshot)
        => string.Equals(options.Provider, "Mock", StringComparison.OrdinalIgnoreCase)
            ? mockGateway
            : new OperatorDirectPayOsGateway(httpClientFactory.CreateClient(ClientName), snapshot);
}
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Infrastructure.Services.Payment;

namespace TripMate.Infrastructure.UnitTests.Payment;

public sealed class OperatorPaymentGatewayFactoryTests
{
    [Fact]
    public void MockConfigurationDoesNotCreatePayOsClient()
    {
        var options = new PaymentOptions { Provider = "Mock" };
        var factory = new OperatorPaymentGatewayFactory(new RejectHttpFactory(), options, new MockPaymentGateway(options));
        Assert.IsType<MockPaymentGateway>(factory.Create(new("client", "api", "checksum", "https://example.test/return", "https://example.test/cancel")));
    }

    private sealed class RejectHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Mock must never create a provider client.");
    }
}
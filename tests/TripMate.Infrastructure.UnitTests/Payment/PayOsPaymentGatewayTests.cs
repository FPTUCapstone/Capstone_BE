using System.Net;
using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Infrastructure.Services.Payment;

namespace TripMate.Infrastructure.UnitTests.Payment;

public sealed class PayOsPaymentGatewayTests
{
    [Fact]
    public async Task CreatePaymentLinkAsync_ReturnsCheckoutUrl_ForSuccessfulPayOsResponse()
    {
        using var handler = new StubHandler(_ => JsonResponse("""
            {"code":"00","desc":"success","data":{"paymentLinkId":"link-1","checkoutUrl":"https://pay.payos.vn/web/link-1","expiredAt":1792315000}}
            """));
        using var client = new HttpClient(handler);
        var gateway = new PayOsPaymentGateway(client, CreateOptions());

        var result = await gateway.CreatePaymentLinkAsync(CreateRequest(), CancellationToken.None);

        result.PaymentLinkId.Should().Be("link-1");
        result.CheckoutUrl.Should().Be("https://pay.payos.vn/web/link-1");
        result.ExpiresAtUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792315000));
    }

    [Fact]
    public async Task CreatePaymentLinkAsync_ThrowsPayOsCodeAndDescription_WhenGatewayRejectsRequest()
    {
        using var handler = new StubHandler(_ => JsonResponse("""
            {"code":"20","desc":"The order code already exists","data":null}
            """));
        using var client = new HttpClient(handler);
        var gateway = new PayOsPaymentGateway(client, CreateOptions());

        var action = () => gateway.CreatePaymentLinkAsync(CreateRequest(), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*code 20*The order code already exists*");
    }

    private static PaymentOptions CreateOptions() => new()
    {
        PayOs = new PayOsOptions
        {
            ClientId = "client-id",
            ApiKey = "api-key",
            ChecksumKey = "checksum-key",
            ReturnUrl = "https://example.com/return",
            CancelUrl = "https://example.com/cancel",
        },
    };

    private static PaymentLinkRequest CreateRequest() => new(
        BookingId: 1,
        OrderCode: 1792314000,
        Amount: 3750000,
        Currency: "VND",
        Description: "Payment for BK-20261018-00002",
        ExpiresAtUtc: DateTimeOffset.FromUnixTimeSeconds(1792315000));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}

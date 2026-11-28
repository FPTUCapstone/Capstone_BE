using System.Net;
using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Infrastructure.Services.Payment;

namespace TripMate.Infrastructure.UnitTests.Payment;

public sealed class PayOsPaymentGatewayTests
{
    [Theory]
    [InlineData("CANCELLED", true)]
    [InlineData("PAID", false)]
    [InlineData("PENDING", false)]
    public async Task RecoverCommissionLink_VerifiesCancellationResult(string finalStatus, bool safe)
    {
        var calls = 0;
        using var handler = new StubHandler(request =>
        {
            calls++;
            var status = request.Method == HttpMethod.Get ? "PENDING" : finalStatus;
            return JsonResponse(JsonSerializer.Serialize(new { code = "00", data = new { orderCode = 8000000000000001L, amountPaid = 0L, status } }));
        });
        using var client = new HttpClient(handler);
        var gateway = new PayOsPaymentGateway(client, CreateOptions());
        var action = () => gateway.CancelCommissionPaymentLinkAsync(new(8000000000000001, null, "Replace expired commission payment"), CancellationToken.None);
        if (safe) await action.Should().NotThrowAsync();
        else await action.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData("CANCELLED", 0, true)]
    [InlineData("PAID", 10000, false)]
    [InlineData("PROCESSING", 0, false)]
    [InlineData("CANCELLED", 1, false)]
    public async Task RecoverCommissionLink_OnlyReleasesConfirmedUnpaidCancelledOrders(string status, long paid, bool safe)
    {
        using var handler = new StubHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            return JsonResponse(JsonSerializer.Serialize(new { code = "00", data = new { orderCode = 8000000000000001L, amountPaid = paid, status } }));
        });
        using var client = new HttpClient(handler);
        var gateway = new PayOsPaymentGateway(client, CreateOptions());
        var action = () => gateway.CancelCommissionPaymentLinkAsync(new(8000000000000001, null, "Replace expired commission payment"), CancellationToken.None);
        if (safe) await action.Should().NotThrowAsync();
        else await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task RecoverCommissionLink_DistinguishesProviderNotFoundFromUnavailable(HttpStatusCode status, bool safe)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"code\":\"error\",\"desc\":\"provider response\"}"),
        });
        using var client = new HttpClient(handler);
        var gateway = new PayOsPaymentGateway(client, CreateOptions());
        var action = () => gateway.CancelCommissionPaymentLinkAsync(new(8000000000000001, null, "Replace expired commission payment"), CancellationToken.None);
        if (safe) await action.Should().NotThrowAsync();
        else await action.Should().ThrowAsync<HttpRequestException>();
    }

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

    [Fact]
    public async Task CancelPaymentLinkAsync_CallsPayOsCancellationEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        var requestBody = string.Empty;
        using var handler = new StubHandler(request =>
        {
            capturedRequest = request;
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {"code":"00","desc":"success","data":{"status":"CANCELLED"}}
                """);
        });
        using var client = new HttpClient(handler);
        var gateway = new PayOsPaymentGateway(client, CreateOptions());

        await gateway.CancelPaymentLinkAsync(
            new PaymentLinkCancellationRequest(1792314000, "link-1", "Traveler cancelled payment"),
            CancellationToken.None);

        capturedRequest!.Method.Should().Be(HttpMethod.Post);
        capturedRequest.RequestUri!.ToString().Should().Be(
            "https://api-merchant.payos.vn/v2/payment-requests/link-1/cancel");
        requestBody.Should().Contain("Traveler cancelled payment");
    }

    [Fact]
    public async Task SubmitRefundAsync_RequiresManualReview_InsteadOfFalselyReportingAFailedAutomaticRefund()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("No network call is expected."));
        using var client = new HttpClient(handler);
        var gateway = new PayOsPaymentGateway(client, CreateOptions());

        RefundSubmissionResult result = await gateway.SubmitRefundAsync(new RefundSubmissionRequest(
            RefundId: 1, BookingId: 2, Amount: 50_000m, Currency: "VND",
            OriginalGatewayTransactionReference: "payment-ref", Reason: "Cancellation",
            IdempotencyKey: "tripmate-refund-1"), CancellationToken.None);

        result.IsAccepted.Should().BeFalse();
        result.RequiresManualReview.Should().BeTrue();
        result.FailureCode.Should().Be("payment.refund.manual_review_required");
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
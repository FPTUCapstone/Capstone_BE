using System.Net;
using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.Services.Payment;

namespace TripMate.Infrastructure.UnitTests.Payment;

public sealed class PayOsChannelVerifierTests
{
    private const string Webhook = "https://tripmate.example/api/v1/payments/payos/channel/7/webhook";
    private static readonly PaymentChannelCredentials Credentials = new("test-client", "test-api", "test-checksum", "123456", "TO COMPANY", "970422");

    [Fact]
    public async Task RegistersTheActualChannelAndRequiresManualOwnershipApproval()
    {
        var handler = new Handler(async request =>
        {
            request.RequestUri!.ToString().Should().Be("https://api-merchant.payos.vn/confirm-webhook");
            request.Headers.GetValues("x-client-id").Single().Should().Be(Credentials.ClientId);
            request.Headers.GetValues("x-api-key").Single().Should().Be(Credentials.ApiKey);
            var body = await request.Content!.ReadAsStringAsync();
            JsonDocument.Parse(body).RootElement.GetProperty("webhookUrl").GetString().Should().Be(Webhook);
            body.Should().NotContain(Credentials.ChecksumKey);
            return Success();
        });
        var result = await new PayOsChannelVerifier(new HttpClient(handler)).VerifyAsync(Credentials, Webhook, default);
        result.Outcome.Should().Be(ChannelVerificationOutcome.PendingAdminApproval);
        result.WebhookUrl.Should().Be(Webhook);
    }

    [Theory]
    [InlineData("http://tripmate.example/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://user:password@tripmate.example/hook")]
    [InlineData("https://tripmate.example/hook?key=secret")]
    public async Task InvalidUrlDoesNotContactProvider(string url)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not contact provider"));
        var result = await new PayOsChannelVerifier(new HttpClient(handler)).VerifyAsync(Credentials, url, default);
        result.Outcome.Should().Be(ChannelVerificationOutcome.ConnectionError);
    }

    [Theory]
    [InlineData(401, "{\"desc\":\"private provider response\"}")]
    [InlineData(200, "{\"code\":\"01\",\"desc\":\"private provider response\"}")]
    [InlineData(200, "not-json")]
    [InlineData(200, "{\"code\":\"00\",\"data\":{\"webhookUrl\":\"https://wrong.example/hook\"}}")]
    public async Task ProviderFailureOrMalformedResultCannotVerify(int status, string body)
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }));
        var result = await new PayOsChannelVerifier(new HttpClient(handler)).VerifyAsync(Credentials, Webhook, default);
        result.Outcome.Should().Be(ChannelVerificationOutcome.ConnectionError);
        result.ToString().Should().NotContain("private provider response").And.NotContain(Credentials.ApiKey);
    }

    [Fact]
    public async Task MalformedHeaderCredentialsAreRejectedWithoutContactingProvider()
    {
        var calls = 0;
        var verifier = new PayOsChannelVerifier(new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Success()); })));
        var result = await verifier.VerifyAsync(Credentials with { ApiKey = "bad\r\nheader" }, Webhook, default);
        result.Outcome.Should().Be(ChannelVerificationOutcome.ConnectionError);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task NetworkTimeoutIsSanitizedButCallerCancellationPropagates()
    {
        var verifier = new PayOsChannelVerifier(new HttpClient(new Handler(_ => throw new TaskCanceledException("secret"))));
        (await verifier.VerifyAsync(Credentials, Webhook, default)).Outcome.Should().Be(ChannelVerificationOutcome.ConnectionError);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(Credentials, Webhook, cancelled.Token));
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { code = "00", data = new { webhookUrl = Webhook, accountNumber = "123456", accountName = "TO COMPANY" } })),
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => run(request);
    }
}

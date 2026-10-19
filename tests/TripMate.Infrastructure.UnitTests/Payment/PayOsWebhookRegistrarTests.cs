using System.Net;
using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Infrastructure.Services.Payment;

namespace TripMate.Infrastructure.UnitTests.Payment;

public sealed class PayOsWebhookRegistrarTests
{
    [Fact]
    public void ApplyLegacyPayOs_FillsOnlyMissingCredentials()
    {
        var options = CreateOptions();
        options.PayOs.ClientId = string.Empty;
        options.PayOs.ApiKey = "configured-api-key";
        options.PayOs.ChecksumKey = string.Empty;

        PaymentOptionsConfiguration.ApplyLegacyPayOs(options, new PayOsOptions
        {
            ClientId = "legacy-client-id",
            ApiKey = "legacy-api-key",
            ChecksumKey = "legacy-checksum-key",
        });

        options.PayOs.ClientId.Should().Be("legacy-client-id");
        options.PayOs.ApiKey.Should().Be("configured-api-key");
        options.PayOs.ChecksumKey.Should().Be("legacy-checksum-key");
    }

    [Fact]
    public async Task ConfirmAsync_SendsConfiguredHeadersAndWebhookUrl()
    {
        CapturedRequest? captured = null;
        using var handler = new StubHandler(request =>
        {
            captured = new CapturedRequest(
                request.RequestUri,
                request.Headers.GetValues("x-client-id").Single(),
                request.Headers.GetValues("x-api-key").Single(),
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":\"00\"}"),
            };
        });
        using var client = new HttpClient(handler);
        var registrar = new PayOsWebhookRegistrar(client, CreateOptions());

        await registrar.ConfirmAsync(
            new Uri("https://example.trycloudflare.com/api/v1/payments/payos/webhook"),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.RequestUri.Should().Be(new Uri("https://api-merchant.payos.vn/confirm-webhook"));
        captured.ClientId.Should().Be("client-id");
        captured.ApiKey.Should().Be("api-key");
        using var document = JsonDocument.Parse(captured.Body);
        document.RootElement.EnumerateObject().Should().ContainSingle();
        document.RootElement.GetProperty("webhookUrl").GetString()
            .Should().Be("https://example.trycloudflare.com/api/v1/payments/payos/webhook");
    }

    [Fact]
    public async Task ConfirmAsync_RejectsNonHttpsUrlBeforeSendingRequest()
    {
        using var handler = new StubHandler(_ => throw new InvalidOperationException("must not send"));
        using var client = new HttpClient(handler);
        var registrar = new PayOsWebhookRegistrar(client, CreateOptions());

        var action = () => registrar.ConfirmAsync(
            new Uri("http://localhost:5000/api/v1/payments/payos/webhook"),
            CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*HTTPS*");
    }

    [Fact]
    public async Task ConfirmAsync_ThrowsStatusOnlyForPayOsFailure()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream response containing no credentials"),
        });
        using var client = new HttpClient(handler);
        var registrar = new PayOsWebhookRegistrar(client, CreateOptions());

        var action = () => registrar.ConfirmAsync(
            new Uri("https://example.trycloudflare.com/api/v1/payments/payos/webhook"),
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*400*");
        exception.Which.Message.Should().NotContain("upstream response");
    }

    private static PaymentOptions CreateOptions() => new()
    {
        Provider = "PayOS",
        PayOs = new PayOsOptions
        {
            ClientId = "client-id",
            ApiKey = "api-key",
            ChecksumKey = "checksum-key",
        },
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed record CapturedRequest(Uri? RequestUri, string ClientId, string ApiKey, string Body);
}
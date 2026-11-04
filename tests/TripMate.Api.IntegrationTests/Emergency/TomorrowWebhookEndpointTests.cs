using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Weather;

namespace TripMate.Api.IntegrationTests.Emergency;

[Collection(nameof(TripMateApiFactory))]
public sealed class TomorrowWebhookEndpointTests
{
    private const string Secret = "tomorrow-test-secret-abcdef12345";

    private static TripMateApiFactory CreateFactory(int maxPayloadBytes = 1_048_576)
    {
        return new TripMateApiFactory(
            configureTestServices: services =>
            {
                services.Configure<TomorrowIoOptions>(opts =>
                {
                    opts.WebhookSecret = Secret;
                    opts.WebhookToleranceSeconds = 300;
                    opts.MaxPayloadSizeBytes = maxPayloadBytes;
                });
            });
    }

    [Fact]
    public async Task ReceiveAlert_WithValidSignature_Returns202AndPersistsInboxItem()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        string payload = """{"id":"notif-1001","event":{"type":"Typhoon","severity":"Extreme"}}""";
        var authenticator = factory.Services.GetRequiredService<ITomorrowWebhookAuthenticator>();
        string signatureHeader = authenticator.GenerateSignatureHeader(payload, DateTimeOffset.UtcNow);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Signature", signatureHeader);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        string responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        doc.RootElement.GetProperty("inboxId").GetInt64().Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Pending");
        doc.RootElement.GetProperty("isDuplicate").GetBoolean().Should().BeFalse();

        // Verify database persistence
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var item = await db.WeatherAlertInboxItems
                .FirstOrDefaultAsync(x => x.ProviderNotificationId == "notif-1001");
            item.Should().NotBeNull();
            item!.Payload.Should().Be(payload);
            item.Status.Should().Be("Pending");
            item.Provider.Should().Be("TomorrowIo");
        }
    }

    [Fact]
    public async Task ReceiveAlert_WhenDuplicateSent_Returns202WithoutDuplicateRow()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        string payload = """{"id":"notif-dup-999","event":{"type":"Flood"}}""";
        var authenticator = factory.Services.GetRequiredService<ITomorrowWebhookAuthenticator>();
        string signatureHeader = authenticator.GenerateSignatureHeader(payload, DateTimeOffset.UtcNow);

        // First delivery
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req1.Headers.Add("X-Signature", signatureHeader);
        using var res1 = await client.SendAsync(req1);
        res1.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Second delivery (duplicate)
        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req2.Headers.Add("X-Signature", signatureHeader);
        using var res2 = await client.SendAsync(req2);
        res2.StatusCode.Should().Be(HttpStatusCode.Accepted);

        string responseJson = await res2.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        doc.RootElement.GetProperty("isDuplicate").GetBoolean().Should().BeTrue();

        // Verify only 1 row exists
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            int count = await db.WeatherAlertInboxItems
                .CountAsync(x => x.ProviderNotificationId == "notif-dup-999");
            count.Should().Be(1);
        }
    }

    [Fact]
    public async Task ReceiveAlert_WithMissingSignature_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        string payload = """{"id":"notif-1002"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReceiveAlert_WithInvalidSignature_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        string payload = """{"id":"notif-1003"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Signature", "t=1730512800,sig=00112233445566778899aabbccddeeff");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReceiveAlert_WithExpiredTimestamp_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        string payload = """{"id":"notif-1004"}""";
        var authenticator = factory.Services.GetRequiredService<ITomorrowWebhookAuthenticator>();
        var expiredTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        string signatureHeader = authenticator.GenerateSignatureHeader(payload, expiredTime);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Signature", signatureHeader);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReceiveAlert_WithNonJsonContentType_Returns415()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent("some text payload", Encoding.UTF8, "text/plain")
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task ReceiveAlert_WithOversizedPayload_Returns413()
    {
        using var factory = CreateFactory(maxPayloadBytes: 50);
        using var client = factory.CreateClient();

        string bigPayload = """{"id":"notif-1005","data":"this payload is definitely much longer than 50 bytes"}""";
        var authenticator = factory.Services.GetRequiredService<ITomorrowWebhookAuthenticator>();
        string signatureHeader = authenticator.GenerateSignatureHeader(bigPayload, DateTimeOffset.UtcNow);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(bigPayload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Signature", signatureHeader);

        using var response = await client.SendAsync(request);

        ((int)response.StatusCode).Should().Be(StatusCodes.Status413PayloadTooLarge);
    }
}

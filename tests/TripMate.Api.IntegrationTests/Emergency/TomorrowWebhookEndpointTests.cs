using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Api.Controllers.V1;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Weather;
using TripMate.Tests.Shared;

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
    public async Task ReceiveAlert_SignedExactlyAsTheTomorrowDocsSample_Returns202()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        string payload = TomorrowAlertFixtures.Webhook();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Signature",
            TomorrowAlertFixtures.SignatureHeader(Secret, DateTimeOffset.UtcNow));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task ReceiveAlert_WithTheRetiredTimestampDotBodySignature_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        string payload = TomorrowAlertFixtures.Webhook();
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        string legacy = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Signature", $"t={timestamp},sig={legacy}");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.WithDbContextAsync(db => db.WeatherAlertInboxItems.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ReceiveAlert_TheSignatureCoversTheTimestampOnly_SoAnotherBodyIsAcceptedWithTheSameHeader()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        string header = TomorrowAlertFixtures.SignatureHeader(Secret, DateTimeOffset.UtcNow);

        foreach (string payload in new[] { TomorrowAlertFixtures.Webhook("PRIOR"), TomorrowAlertFixtures.Webhook("START") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Signature", header);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        (await factory.WithDbContextAsync(db => db.WeatherAlertInboxItems.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task ReceiveAlert_WithValidSignature_Returns202AndPersistsInboxItem()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        string payload = TomorrowAlertFixtures.Webhook("START");
        string signatureHeader = TomorrowAlertFixtures.SignatureHeader(Secret, DateTimeOffset.UtcNow);

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
                .SingleOrDefaultAsync();
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

        string payload = TomorrowAlertFixtures.Webhook("PRIOR");
        string signatureHeader = TomorrowAlertFixtures.SignatureHeader(Secret, DateTimeOffset.UtcNow);

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
                .CountAsync();
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
        string signatureHeader = TomorrowAlertFixtures.SignatureHeader(Secret, DateTimeOffset.UtcNow.AddMinutes(-10));

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

    private sealed class UnknownLengthContent(int totalBytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[totalBytes]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class EndlessBody : Stream
    {
        public long BytesRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BytesRead += buffer.Length;
            return ValueTask.FromResult(BytesRead > 64L * 1024 * 1024 ? 0 : buffer.Length);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReceiveAlert_WithChunkedOversizedBody_Returns413()
    {
        using var factory = CreateFactory(maxPayloadBytes: 1024);
        using var client = factory.CreateClient();
        var content = new UnknownLengthContent(4096);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = content,
        };

        using var response = await client.SendAsync(request);

        ((int)response.StatusCode).Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task ReceiveAlert_WithBodyOfUnknownLength_StopsReadingAtTheLimit()
    {
        const int limit = 1024;
        var options = Options.Create(new TomorrowIoOptions { WebhookSecret = Secret, MaxPayloadSizeBytes = limit });
        var body = new EndlessBody();
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        var controller = new TomorrowWebhooksController(
            null!,
            new TomorrowWebhookAuthenticator(options),
            options,
            NullLogger<TomorrowWebhooksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };

        var result = await controller.ReceiveAlertAsync(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        body.BytesRead.Should().BeLessThanOrEqualTo(limit + 16 * 1024, "one read chunk past the limit is the most it may hold");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    public async Task ReceiveAlert_WithSignedEmptyOrMalformedBody_Returns400AndStoresNothing(string payload)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        string signatureHeader = TomorrowAlertFixtures.SignatureHeader(Secret, DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Signature", signatureHeader);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await factory.WithDbContextAsync(db => db.WeatherAlertInboxItems.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ReceiveAlert_WithUnsignedMalformedBody_StillReturns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ReceiveAlert_WithOversizedPayload_Returns413()
    {
        using var factory = CreateFactory(maxPayloadBytes: 50);
        using var client = factory.CreateClient();

        string bigPayload = """{"id":"notif-1005","data":"this payload is definitely much longer than 50 bytes"}""";
        string signatureHeader = TomorrowAlertFixtures.SignatureHeader(Secret, DateTimeOffset.UtcNow);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(bigPayload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Signature", signatureHeader);

        using var response = await client.SendAsync(request);

        ((int)response.StatusCode).Should().Be(StatusCodes.Status413PayloadTooLarge);
    }
}

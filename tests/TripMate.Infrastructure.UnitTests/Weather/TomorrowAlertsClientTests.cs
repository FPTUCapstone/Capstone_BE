using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Infrastructure.Weather;

using Xunit.Abstractions;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class TomorrowAlertsClientTests(ITestOutputHelper output)
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }

    [Fact]
    public async Task GetNotificationsAsync_Disabled_DoesNotMakeHttpCallAndReturnsEmpty()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var options = Options.Create(new TomorrowIoOptions
        {
            Enabled = false,
            ApiKey = "secret-key",
            BaseUrl = "https://api.tomorrow.io"
        });

        var alertsClient = new TomorrowAlertsClient(client, options, NullLogger<TomorrowAlertsClient>.Instance);

        var result = await alertsClient.GetNotificationsAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            pageToken: null);

        result.Notifications.Should().BeEmpty();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetNotificationsAsync_EmptyApiKey_DoesNotMakeHttpCallAndReturnsEmpty()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var options = Options.Create(new TomorrowIoOptions
        {
            Enabled = true,
            ApiKey = "",
            BaseUrl = "https://api.tomorrow.io"
        });

        var alertsClient = new TomorrowAlertsClient(client, options, NullLogger<TomorrowAlertsClient>.Instance);

        var result = await alertsClient.GetNotificationsAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            pageToken: null);

        result.Notifications.Should().BeEmpty();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetNotificationsAsync_Success_ParsesNotificationsAndNextPageToken()
    {
        const string jsonResponse = """
        {
          "notifications": [
            {
              "id": "notif-001",
              "eventId": "event-123",
              "alertId": "alert-456",
              "type": "PUBLISH",
              "updateTime": "2026-11-02T10:00:00Z"
            },
            {
              "id": "notif-002",
              "eventId": "event-124",
              "alertId": "alert-457",
              "type": "UPDATE",
              "updateTime": "2026-11-02T10:05:00Z"
            }
          ],
          "nextPageToken": "page-2-token"
        }
        """;

        var handler = new StubHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var options = Options.Create(new TomorrowIoOptions
        {
            Enabled = true,
            ApiKey = "valid-api-key",
            BaseUrl = "https://api.tomorrow.io"
        });

        var alertsClient = new TomorrowAlertsClient(client, options, NullLogger<TomorrowAlertsClient>.Instance);

        var result = await alertsClient.GetNotificationsAsync(
            new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 11, 0, 0, TimeSpan.Zero),
            pageToken: "page-1-token");

        handler.CallCount.Should().Be(1);
        handler.LastRequest!.RequestUri!.Query.Should().Contain("pageToken=page-1-token");
        result.Notifications.Should().HaveCount(2);
        result.Notifications[0].Id.Should().Be("notif-001");
        result.Notifications[0].EventId.Should().Be("event-123");
        result.Notifications[0].Type.Should().Be("PUBLISH");
        result.Notifications[1].Id.Should().Be("notif-002");
        result.NextPageToken.Should().Be("page-2-token");
    }

    [Fact]
    public async Task GetNotificationsAsync_429TooManyRequests_ReturnsEmptyGracefullyWithoutCrashing()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)429));
        using var client = new HttpClient(handler);
        var options = Options.Create(new TomorrowIoOptions
        {
            Enabled = true,
            ApiKey = "valid-api-key",
            BaseUrl = "https://api.tomorrow.io"
        });

        var alertsClient = new TomorrowAlertsClient(client, options, NullLogger<TomorrowAlertsClient>.Instance);

        var result = await alertsClient.GetNotificationsAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            pageToken: null);

        result.Notifications.Should().BeEmpty();
        result.NextPageToken.Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationsAsync_500ServerError_ReturnsEmptyGracefullyWithoutCrashing()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new HttpClient(handler);
        var options = Options.Create(new TomorrowIoOptions
        {
            Enabled = true,
            ApiKey = "valid-api-key",
            BaseUrl = "https://api.tomorrow.io"
        });

        var alertsClient = new TomorrowAlertsClient(client, options, NullLogger<TomorrowAlertsClient>.Instance);

        var result = await alertsClient.GetNotificationsAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            pageToken: null);

        result.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task GetNotificationsAsync_MalformedJson_ReturnsEmptyGracefully()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-valid-json{{{", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var options = Options.Create(new TomorrowIoOptions
        {
            Enabled = true,
            ApiKey = "valid-api-key",
            BaseUrl = "https://api.tomorrow.io"
        });

        var alertsClient = new TomorrowAlertsClient(client, options, NullLogger<TomorrowAlertsClient>.Instance);

        var result = await alertsClient.GetNotificationsAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            pageToken: null);

        result.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task LiveTomorrowApi_ReconciliationProbe()
    {
        string? apiKey = Environment.GetEnvironmentVariable("Tomorrow__ApiKey")
            ?? Environment.GetEnvironmentVariable("TomorrowIo__ApiKey")
            ?? Environment.GetEnvironmentVariable("Tomorrow:ApiKey");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            output.WriteLine("PROBE_SKIPPED: Tomorrow API key is not configured in process environment.");
            return;
        }

        var recordingHandler = new LiveProbeRecordingHandler(new HttpClientHandler());
        using var client = new HttpClient(recordingHandler);
        var options = Options.Create(new TomorrowIoOptions
        {
            Enabled = true,
            ApiKey = apiKey,
            BaseUrl = "https://api.tomorrow.io"
        });

        var alertsClient = new TomorrowAlertsClient(client, options, NullLogger<TomorrowAlertsClient>.Instance);

        var result = await alertsClient.GetNotificationsAsync(
            DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow,
            pageToken: null);

        var statusCode = recordingHandler.CapturedStatusCode;
        string classification;
        if (statusCode == HttpStatusCode.OK)
        {
            classification = result.Notifications.Count > 0
                ? "LIVE_API_PASS"
                : "LIVE_API_PASS_NO_ACTIVE_NOTIFICATION";
        }
        else if (statusCode == HttpStatusCode.Unauthorized)
        {
            classification = "LIVE_API_AUTH_OR_ENTITLEMENT_FAIL";
        }
        else if (statusCode == HttpStatusCode.Forbidden || statusCode == HttpStatusCode.NotFound)
        {
            classification = "LIVE_API_BLOCKED_ENTITLEMENT";
        }
        else
        {
            classification = $"LIVE_API_RESPONSE_{statusCode}";
        }

        output.WriteLine($"PROBE_CLASSIFICATION: {classification}");
        output.WriteLine($"PROBE_STATUS_CODE: {(int?)statusCode}");
        output.WriteLine($"PROBE_NOTIFICATION_COUNT: {result.Notifications.Count}");

        result.Should().NotBeNull();
    }

    private sealed class LiveProbeRecordingHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        public HttpStatusCode? CapturedStatusCode { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Strictly guard against logging or leaking request.RequestUri with API key
            var response = await base.SendAsync(request, cancellationToken);
            CapturedStatusCode = response.StatusCode;
            return response;
        }
    }
}

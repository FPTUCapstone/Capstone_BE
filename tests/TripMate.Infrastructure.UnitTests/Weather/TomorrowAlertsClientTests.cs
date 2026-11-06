using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Infrastructure.Weather;
using TripMate.Tests.Shared;

namespace TripMate.Infrastructure.UnitTests.Weather;

/// <summary>Requests and responses follow https://docs.tomorrow.io/reference (v4).</summary>
public sealed class TomorrowAlertsClientTests
{
    private const string ApiKey = "test-api-key-not-real";
    private const string AlertId = "alert-123";

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? ApiKeyHeader, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(),
                request.Headers.TryGetValues("apikey", out var values) ? values.Single() : null, body));
            return responder(request);
        }
    }

    private sealed class CapturingLogger : ILogger<TomorrowAlertsClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception);
    }

    private static (TomorrowAlertsClient Client, RecordingHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        bool enabled = true,
        string apiKey = ApiKey,
        string alertId = AlertId,
        ILogger<TomorrowAlertsClient>? logger = null)
    {
        var handler = new RecordingHandler(responder);
        var client = new TomorrowAlertsClient(new HttpClient(handler), Options.Create(new TomorrowIoOptions
        {
            Enabled = enabled,
            ApiKey = apiKey,
            AlertId = alertId,
            BaseUrl = "https://api.tomorrow.io",
        }), logger ?? NullLogger<TomorrowAlertsClient>.Instance);
        return (client, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task LinkLocations_PostsTheDocumentedBodyToTheConfiguredAlert_AndAccepts204()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var outcome = await client.LinkLocationsAsync(["loc-1", "loc-2"]);

        outcome.Should().Be(TomorrowCallOutcome.Success);
        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Url.Should().Be("https://api.tomorrow.io/v4/alerts/alert-123/locations/link");
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("locations").EnumerateArray()
            .Select(id => id.GetString()).Should().Equal("loc-1", "loc-2");
    }

    [Fact]
    public async Task UnlinkLocations_PostsToTheUnlinkEndpoint()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        (await client.UnlinkLocationsAsync(["loc-1"])).Should().Be(TomorrowCallOutcome.Success);

        handler.Requests.Single().Url.Should().Be("https://api.tomorrow.io/v4/alerts/alert-123/locations/unlink");
        handler.Requests.Single().Body.Should().Be("""{"locations":["loc-1"]}""");
    }

    [Fact]
    public async Task GetLinkedLocations_ReadsDataLocations()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, """{"data":{"locations":["5e82fb82b66492001218aaf3","b"]}}"""));

        var linked = await client.GetLinkedLocationIdsAsync();

        linked.Outcome.Should().Be(TomorrowCallOutcome.Success);
        linked.LocationIds.Should().BeEquivalentTo("5e82fb82b66492001218aaf3", "b");
        handler.Requests.Single().Method.Should().Be(HttpMethod.Get);
        handler.Requests.Single().Url.Should().Be("https://api.tomorrow.io/v4/alerts/alert-123/locations");
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"locations":["a"]}""")]
    [InlineData("not json")]
    public async Task GetLinkedLocations_WithAnUndocumentedBody_IsNotASuccess(string body)
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, body));

        (await client.GetLinkedLocationIdsAsync()).Outcome.Should().Be(TomorrowCallOutcome.InvalidResponse);
    }

    [Fact]
    public async Task CreateLocation_SendsAGeoJsonPointInLonLatOrder_AndReadsDataLocationId()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK,
            """{"data":{"location":{"id":"5e82fb82b66492001218aaf3","name":"Meeting","geometry":{"type":"Point","coordinates":[108.2,16.05]}}}}"""));

        var created = await client.CreateLocationAsync("Meeting", latitude: 16.05, longitude: 108.2);

        created.Should().Be(new TomorrowCreatedLocation(TomorrowCallOutcome.Success, "5e82fb82b66492001218aaf3"));
        var request = handler.Requests.Single();
        request.Url.Should().Be("https://api.tomorrow.io/v4/locations");
        var geometry = JsonDocument.Parse(request.Body!).RootElement.GetProperty("geometry");
        geometry.GetProperty("type").GetString().Should().Be("Point");
        geometry.GetProperty("coordinates").EnumerateArray().Select(value => value.GetDouble()).Should().Equal(108.2, 16.05);
    }

    [Fact]
    public async Task CreateLocation_WithoutDataLocationId_IsAnInvalidResponse()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, """{"id":"top-level-is-not-the-contract"}"""));

        (await client.CreateLocationAsync("Meeting", 16, 108)).Outcome.Should().Be(TomorrowCallOutcome.InvalidResponse);
    }

    [Fact]
    public async Task DeleteLocation_CallsTheLocationResource()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        (await client.DeleteLocationAsync("loc-1")).Should().Be(TomorrowCallOutcome.Success);

        handler.Requests.Single().Method.Should().Be(HttpMethod.Delete);
        handler.Requests.Single().Url.Should().Be("https://api.tomorrow.io/v4/locations/loc-1");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TomorrowCallOutcome.NotAuthorized)]
    [InlineData(HttpStatusCode.Forbidden, TomorrowCallOutcome.NotAuthorized)]
    [InlineData(HttpStatusCode.NotFound, TomorrowCallOutcome.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, TomorrowCallOutcome.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, TomorrowCallOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, TomorrowCallOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, TomorrowCallOutcome.InvalidResponse)]
    public async Task ProviderStatus_IsClassified(HttpStatusCode status, TomorrowCallOutcome expected)
    {
        var (client, _) = Create(_ => new HttpResponseMessage(status));

        (await client.LinkLocationsAsync(["loc-1"])).Should().Be(expected);
        (await client.UnlinkLocationsAsync(["loc-1"])).Should().Be(expected);
        (await client.DeleteLocationAsync("loc-1")).Should().Be(expected);
        (await client.GetLinkedLocationIdsAsync()).Outcome.Should().Be(expected);
        (await client.CreateLocationAsync("Meeting", 16, 108)).Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task NetworkFailure_IsUnavailable()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        (await client.LinkLocationsAsync(["loc-1"])).Should().Be(TomorrowCallOutcome.Unavailable);
    }

    [Fact]
    public async Task ApiKey_TravelsInTheHeaderAndNeverInTheUrlOrLogs()
    {
        var logger = new CapturingLogger();
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.Forbidden), logger: logger);

        await client.LinkLocationsAsync(["loc-1"]);
        await client.GetNotificationsAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null);

        handler.Requests.Should().OnlyContain(request => request.ApiKeyHeader == ApiKey && !request.Url.Contains(ApiKey));
        logger.Messages.Should().NotBeEmpty().And.OnlyContain(message => !message.Contains(ApiKey));
    }

    [Theory]
    [InlineData(false, ApiKey, AlertId)]
    [InlineData(true, "", AlertId)]
    [InlineData(true, ApiKey, "")]
    public async Task WithoutCompleteConfiguration_NothingIsSent(bool enabled, string apiKey, string alertId)
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK), enabled, apiKey, alertId);

        (await client.LinkLocationsAsync(["loc-1"])).Should().Be(TomorrowCallOutcome.Skipped);
        (await client.GetNotificationsAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null))
            .Notifications.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetNotifications_FiltersByTheConfiguredAlert_AndReadsDataNotifications()
    {
        string item = TomorrowAlertFixtures.ListItem(alertId: AlertId);
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK,
            TomorrowAlertFixtures.ListResponse("/v4/notifications?limit=100&marker=9205e5719550aa00085ba8d5&offset=100", item)));

        var response = await client.GetNotificationsAsync(
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero), null);

        handler.Requests.Single().Url.Should().Be(
            "https://api.tomorrow.io/v4/notifications?startTime=2026-11-02T10%3A00%3A00Z&endTime=2026-11-02T12%3A00%3A00Z&alertIds=alert-123");
        response.ProviderUnavailable.Should().BeFalse();
        var notification = response.Notifications.Single();
        notification.Id.Should().Be("61d2e5719550aa00085ac502");
        notification.AlertId.Should().Be(AlertId);
        JsonDocument.Parse(notification.RawJson).RootElement.GetProperty("type").GetString().Should().Be("START");
        response.NextPageToken.Should().Be("/v4/notifications?limit=100&marker=9205e5719550aa00085ba8d5&offset=100");
    }

    [Fact]
    public async Task GetNotifications_NextPage_KeepsTheFiltersAndReusesOnlyThePagingCursor()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, TomorrowAlertFixtures.ListResponse(null)));

        await client.GetNotificationsAsync(
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero),
            "/v4/notifications?limit=100&marker=9205e5719550aa00085ba8d5&offset=100");

        handler.Requests.Single().Url.Should().Be(
            "https://api.tomorrow.io/v4/notifications?startTime=2026-11-02T10%3A00%3A00Z&endTime=2026-11-02T12%3A00%3A00Z&alertIds=alert-123&limit=100&marker=9205e5719550aa00085ba8d5&offset=100");
    }

    [Fact]
    public async Task GetNotifications_RefusesANextLinkToAnotherHostOrPath()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, TomorrowAlertFixtures.ListResponse(null)));

        var response = await client.GetNotificationsAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow,
            "https://attacker.example/v4/notifications?offset=100");

        response.ProviderUnavailable.Should().BeTrue();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetNotifications_EmptyPage_EndsPagination()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, TomorrowAlertFixtures.ListResponse("/v4/notifications?offset=200")));

        var response = await client.GetNotificationsAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null);

        response.Notifications.Should().BeEmpty();
        response.NextPageToken.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task GetNotifications_ProviderFailure_KeepsTheWindowForTheNextSweep(HttpStatusCode status)
    {
        var (client, _) = Create(_ => new HttpResponseMessage(status));

        (await client.GetNotificationsAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null))
            .ProviderUnavailable.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"notifications":[]}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("not json")]
    public async Task GetNotifications_UndocumentedBody_IsNotTreatedAsAnEmptySuccess(string body)
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, body));

        (await client.GetNotificationsAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null))
            .ProviderUnavailable.Should().BeTrue();
    }
}

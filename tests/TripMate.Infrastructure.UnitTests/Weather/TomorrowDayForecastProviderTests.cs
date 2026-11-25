using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Itineraries.WeatherTips;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class TomorrowDayForecastProviderTests
{
    private const string FullHour = """
        {"timelines":{"hourly":[
          {"time":"2026-11-25T03:00:00Z","values":{"temperature":27.4,"temperatureApparent":33.2,"precipitationProbability":55,
            "rainIntensity":0.6,"uvIndex":8,"windSpeed":4.5,"weatherCode":8000}}
        ]}}
        """;

    [Fact]
    public async Task GetHourly_ParsesAllFields()
    {
        using var provider = CreateProvider(new StubHandler(FullHour));

        var result = await provider.GetHourlyAsync(16.1m, 108.2m, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().Be(new HourlyForecast(
            new DateTimeOffset(2026, 11, 25, 3, 0, 0, TimeSpan.Zero),
            TemperatureC: 27.4,
            ApparentTemperatureC: 33.2,
            RainProbabilityPercent: 55,
            RainMmPerHour: 0.6,
            UvIndex: 8,
            WindSpeedMps: 4.5,
            Thunderstorm: true));
    }

    [Fact]
    public async Task GetHourly_UsesForecastEndpoint_HourlyMetric_AndKeepsKeyOutOfUrl()
    {
        var handler = new StubHandler(FullHour);
        using var provider = CreateProvider(handler);

        await provider.GetHourlyAsync(16.1m, 108.2m, CancellationToken.None);

        handler.Path.Should().Be("/v4/weather/forecast");
        handler.Query.Should().Contain("timesteps=1h").And.Contain("units=metric").And.Contain("location=16.1%2C108.2");
        handler.Query.Should().NotContain("server-secret");
        handler.ApiKey.Should().Be("server-secret");
    }

    [Fact]
    public async Task GetHourly_DropsAnHourMissingAField_InsteadOfFillingItIn()
    {
        var handler = new StubHandler("""
            {"timelines":{"hourly":[
              {"time":"2026-11-25T03:00:00Z","values":{"temperature":27,"temperatureApparent":30,"precipitationProbability":10,
                "rainIntensity":0,"windSpeed":3,"weatherCode":1000}},
              {"time":"2026-11-25T04:00:00Z","values":{"temperature":28,"temperatureApparent":31,"precipitationProbability":10,
                "rainIntensity":0,"uvIndex":5,"windSpeed":3,"weatherCode":1000}}
            ]}}
            """);
        using var provider = CreateProvider(handler);

        var result = await provider.GetHourlyAsync(16m, 108m, CancellationToken.None);

        result.Should().ContainSingle().Which.StartUtc.Should().Be(new DateTimeOffset(2026, 11, 25, 4, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task GetHourly_ReturnsHoursInTimeOrder()
    {
        var handler = new StubHandler("""
            {"timelines":{"hourly":[
              {"time":"2026-11-25T05:00:00Z","values":{"temperature":1,"temperatureApparent":1,"precipitationProbability":0,"rainIntensity":0,"uvIndex":0,"windSpeed":0,"weatherCode":1000}},
              {"time":"2026-11-25T04:00:00Z","values":{"temperature":1,"temperatureApparent":1,"precipitationProbability":0,"rainIntensity":0,"uvIndex":0,"windSpeed":0,"weatherCode":1000}}
            ]}}
            """);
        using var provider = CreateProvider(handler);

        var result = await provider.GetHourlyAsync(16m, 108m, CancellationToken.None);

        result!.Select(hour => hour.StartUtc.Hour).Should().Equal(4, 5);
    }

    [Fact]
    public async Task GetHourly_WithoutHourlyTimeline_ReturnsNull()
    {
        using var provider = CreateProvider(new StubHandler("""{"timelines":{}}"""));

        var result = await provider.GetHourlyAsync(16m, 108m, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetHourly_WhenDisabled_ReturnsNullWithoutCalling()
    {
        var handler = new StubHandler(FullHour);
        using var provider = CreateProvider(handler, enabled: false);

        var result = await provider.GetHourlyAsync(16m, 108m, CancellationToken.None);

        result.Should().BeNull();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetHourly_CachesPerLocation()
    {
        var handler = new StubHandler(FullHour);
        using var provider = CreateProvider(handler);

        await provider.GetHourlyAsync(16.1m, 108.2m, CancellationToken.None);
        await provider.GetHourlyAsync(16.1m, 108.2m, CancellationToken.None);
        handler.Calls.Should().Be(1);

        await provider.GetHourlyAsync(15.9m, 108.2m, CancellationToken.None);
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task GetHourly_NonSuccessStatus_Throws()
    {
        using var provider = CreateProvider(new StubHandler("{}", HttpStatusCode.TooManyRequests));

        var act = () => provider.GetHourlyAsync(16m, 108m, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static ProviderScope CreateProvider(StubHandler handler, bool enabled = true)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.tomorrow.io/") };
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new ProviderScope(client, cache, new TomorrowDayForecastProvider(client, cache,
            Options.Create(new TomorrowWeatherOptions { Enabled = enabled, ApiKey = "server-secret" })));
    }

    private sealed class ProviderScope(HttpClient client, MemoryCache cache, TomorrowDayForecastProvider provider)
        : IDisposable
    {
        public Task<IReadOnlyList<HourlyForecast>?> GetHourlyAsync(decimal latitude, decimal longitude, CancellationToken ct) =>
            provider.GetHourlyAsync(latitude, longitude, ct);

        public void Dispose()
        {
            client.Dispose();
            cache.Dispose();
        }
    }

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public string? Query { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Path = request.RequestUri!.AbsolutePath;
            Query = request.RequestUri.Query;
            ApiKey = request.Headers.GetValues("apikey").Single();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
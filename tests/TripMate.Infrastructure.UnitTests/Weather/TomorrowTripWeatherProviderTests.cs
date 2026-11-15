using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class TomorrowTripWeatherProviderTests
{
    [Fact]
    public async Task Forecast_SelectsHourContainingVisit_AndKeepsKeyOutOfUrl()
    {
        var handler = new StubHandler("""
            {"timelines":{"hourly":[
              {"time":"2026-10-28T09:00:00Z","values":{"rainIntensity":3.5,"weatherCode":4001}},
              {"time":"2026-10-28T10:00:00Z","values":{"rainIntensity":12.5,"weatherCode":8000}}
            ]}}
            """);
        using var provider = CreateProvider(handler);

        var result = await provider.GetAsync(new WeatherForecastRequest(
            16.0544m, 108.2022m, new DateTimeOffset(2026, 10, 28, 10, 37, 0, TimeSpan.Zero)),
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.RainMmPerHour.Should().Be(12.5);
        result.Thunderstorm.Should().BeTrue();
        result.ForecastAtUtc.Should().Be(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero));
        result.Basis.Should().Be(TripWeatherBasis.ForecastAtPlannedTime);
        result.OfficialAlerts.Should().BeEmpty();
        handler.Path.Should().Be("/v4/weather/forecast");
        handler.Query.Should().Contain("timesteps=1h");
        handler.Query.Should().Contain("units=metric");
        handler.Query.Should().NotContain("server-secret");
        handler.ApiKey.Should().Be("server-secret");
    }

    [Fact]
    public async Task Realtime_RejectsStaleObservation()
    {
        var handler = new StubHandler("""
            {"data":{"time":"2026-10-28T09:00:00Z","values":{"rainIntensity":50,"weatherCode":8000}}}
            """);
        using var provider = CreateProvider(handler);

        var result = await provider.GetAsync(new WeatherForecastRequest(
            16m, 108m, new DateTimeOffset(2026, 10, 28, 9, 11, 0, TimeSpan.Zero),
            UseRealtime: true), CancellationToken.None);

        result.Should().BeNull();
        handler.Path.Should().Be("/v4/weather/realtime");
    }

    [Fact]
    public async Task Realtime_CacheDoesNotExtendObservationBeyondTenMinutes()
    {
        var handler = new StubHandler("""
            {"data":{"time":"2026-10-28T08:59:00Z","values":{"rainIntensity":20,"weatherCode":4001}}}
            """);
        using var provider = CreateProvider(handler);
        var first = await provider.GetAsync(new WeatherForecastRequest(16m, 108m,
            new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero), true), CancellationToken.None);
        var late = await provider.GetAsync(new WeatherForecastRequest(16m, 108m,
            new DateTimeOffset(2026, 10, 28, 9, 9, 30, TimeSpan.Zero), true), CancellationToken.None);

        first.Should().NotBeNull();
        late.Should().BeNull();
    }

    [Fact]
    public async Task Forecast_MissingRainIntensity_IsUnknown()
    {
        var handler = new StubHandler("""
            {"timelines":{"hourly":[{"time":"2026-10-28T10:00:00Z","values":{"weatherCode":4001}}]}}
            """);
        using var provider = CreateProvider(handler);
        var result = await provider.GetAsync(new WeatherForecastRequest(
            16m, 108m, new DateTimeOffset(2026, 10, 28, 10, 15, 0, TimeSpan.Zero)),
            CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Forecast_MissingWeatherCode_IsUnknown()
    {
        var handler = new StubHandler("""
            {"timelines":{"hourly":[{"time":"2026-10-28T10:00:00Z","values":{"rainIntensity":5}}]}}
            """);
        using var provider = CreateProvider(handler);
        var result = await provider.GetAsync(new WeatherForecastRequest(
            16m, 108m, new DateTimeOffset(2026, 10, 28, 10, 15, 0, TimeSpan.Zero)),
            CancellationToken.None);
        result.Should().BeNull();
    }

    private static ProviderScope CreateProvider(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.tomorrow.io/") };
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new ProviderScope(client, cache, new TomorrowTripWeatherProvider(client, cache,
            Options.Create(new TomorrowWeatherOptions { Enabled = true, ApiKey = "server-secret" })));
    }

    private sealed class ProviderScope(HttpClient client, MemoryCache cache,
        TomorrowTripWeatherProvider provider) : IDisposable
    {
        public Task<TripWeatherSnapshot?> GetAsync(WeatherForecastRequest request, CancellationToken ct) =>
            provider.GetAsync(request, ct);
        public void Dispose() { client.Dispose(); cache.Dispose(); }
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Query { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Query = request.RequestUri.Query;
            ApiKey = request.Headers.GetValues("apikey").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.PublicForecast;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class TomorrowPublicForecastProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 3, 7, 0, TimeSpan.Zero);

    private const string HourValues = """
        "temperature":27.4,"temperatureApparent":33.2,"precipitationProbability":55,"rainIntensity":0.6,
        "uvIndex":8,"windSpeed":4.5,"weatherCode":8000,"humidity":70,"cloudCover":38,"visibility":14.5
        """;

    // Tomorrow.io starts a Vietnam day at 06:00 local, which is 23:00 UTC of the day before.
    private const string DayValues = """
        "temperatureMin":23,"temperatureMax":31,"temperatureApparentMax":33,"precipitationProbabilityMax":35,
        "rainIntensityMax":0.4,"rainAccumulationSum":1.2,"uvIndexMax":8,"windSpeedMax":6.2,"weatherCodeMax":1101,
        "humidityAvg":74,"sunriseTime":"2026-11-25T22:56:00Z","sunsetTime":"2026-11-26T10:18:00Z"
        """;

    private static readonly string Full =
        """{"timelines":{"hourly":[{"time":"2026-11-26T03:00:00Z","values":{""" + HourValues
        + """}}],"daily":[{"time":"2026-11-25T23:00:00Z","values":{""" + DayValues + "}}]}}";

    [Fact]
    public async Task Get_ParsesHourlyAndDaily_InVietnamLocalDates()
    {
        using var scope = Create(new StubHandler(Full));

        var data = await scope.Provider.GetAsync(16.05m, 108.2m, CancellationToken.None);

        data!.FetchedAtUtc.Should().Be(Now);
        data.Hourly.Should().ContainSingle().Which.Should().Be(new PublicWeatherHourDto(
            new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero), 27.4, 33.2, 55, 0.6, 8, 4.5,
            Thunderstorm: true, WeatherCode: 8000, HumidityPercent: 70, CloudCoverPercent: 38, VisibilityKm: 14.5));
        data.Daily.Should().ContainSingle().Which.Should().Be(new PublicWeatherDayDto(
            "2026-11-26", 23, 31, 33, 35, 0.4, 1.2, 8, 6.2, Thunderstorm: false, WeatherCode: 1101,
            HumidityAvgPercent: 74, SunriseTime: "05:56", SunsetTime: "17:18"));
    }

    [Fact]
    public async Task Get_MakesOneForecastCall_WithHourAndDaySteps_AndKeepsKeyOutOfUrl()
    {
        var handler = new StubHandler(Full);
        using var scope = Create(handler);

        await scope.Provider.GetAsync(16.05m, 108.2m, CancellationToken.None);

        handler.Calls.Should().Be(1);
        handler.Path.Should().Be("/v4/weather/forecast");
        handler.Query.Should().Contain("location=16.05%2C108.2").And.Contain("timesteps=1h%2C1d")
            .And.Contain("units=metric").And.NotContain("public-secret");
        handler.ApiKey.Should().Be("public-secret");
    }

    [Fact]
    public async Task Get_DropsPointsMissingAField_InsteadOfFillingThemIn()
    {
        using var scope = Create(new StubHandler("""
            {"timelines":{
              "hourly":[{"time":"2026-11-26T03:00:00Z","values":{"temperature":27}}],
              "daily":[{"time":"2026-11-25T23:00:00Z","values":{"temperatureMin":23}}]
            }}
            """));

        var data = await scope.Provider.GetAsync(16m, 108m, CancellationToken.None);

        data!.Hourly.Should().BeEmpty();
        data.Daily.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_CachesPerCell()
    {
        var handler = new StubHandler(Full);
        using var scope = Create(handler);

        await scope.Provider.GetAsync(16.05m, 108.2m, CancellationToken.None);
        await scope.Provider.GetAsync(16.05m, 108.2m, CancellationToken.None);
        handler.Calls.Should().Be(1);

        await scope.Provider.GetAsync(16.06m, 108.2m, CancellationToken.None);
        handler.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Get_NonSuccess_ReturnsNull_AndIsNotCached(HttpStatusCode status)
    {
        var handler = new StubHandler("{}", status);
        using var scope = Create(handler);

        (await scope.Provider.GetAsync(16m, 108m, CancellationToken.None)).Should().BeNull();
        (await scope.Provider.GetAsync(16m, 108m, CancellationToken.None)).Should().BeNull();

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Get_NetworkFailure_ReturnsNull()
    {
        using var scope = Create(new StubHandler("{}", fail: true));

        (await scope.Provider.GetAsync(16m, 108m, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Get_WithoutHourlyTimeline_ReturnsNull()
    {
        using var scope = Create(new StubHandler("""{"timelines":{}}"""));

        (await scope.Provider.GetAsync(16m, 108m, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Get_WhenDisabled_ReturnsNullWithoutCalling()
    {
        var handler = new StubHandler(Full);
        using var scope = Create(handler, enabled: false);

        (await scope.Provider.GetAsync(16m, 108m, CancellationToken.None)).Should().BeNull();
        handler.Calls.Should().Be(0);
    }

    private static Scope Create(StubHandler handler, bool enabled = true)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.tomorrow.io/") };
        var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new TomorrowPublicForecastProvider(client, cache,
            Options.Create(new PublicWeatherOptions { Enabled = enabled, ApiKey = "public-secret" }),
            new FixedClock(Now), NullLogger<TomorrowPublicForecastProvider>.Instance);
        return new Scope(client, cache, provider);
    }

    private sealed class Scope(HttpClient client, MemoryCache cache, TomorrowPublicForecastProvider provider) : IDisposable
    {
        public TomorrowPublicForecastProvider Provider => provider;

        public void Dispose()
        {
            client.Dispose();
            cache.Dispose();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK, bool fail = false)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public string? Query { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (fail)
            {
                throw new HttpRequestException("network down");
            }

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
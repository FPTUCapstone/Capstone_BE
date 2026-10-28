using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Infrastructure.Weather;

namespace TripMate.Infrastructure.UnitTests.Weather;

public sealed class OpenWeather4TripWeatherProviderTests
{
    [Fact]
    public async Task GetAsync_UsesNearestTimelineRecordAndLoadsOfficialAlertDetails()
    {
        var handler = new WeatherHandler(request => request.AbsolutePath switch
        {
            "/data/4.0/onecall/timeline/15min" => """
                {"data":[
                  {"dt":1792670400,"rain":{"1h":1.2},"weather":[{"id":500,"main":"Rain","description":"light rain"}],"alerts":[]},
                  {"dt":1792671300,"rain":{"1h":12.5},"weather":[{"id":201,"main":"Thunderstorm","description":"storm"}],"alerts":["alert-1"]}
                ]}
                """,
            "/data/4.0/onecall/alert/alert-1" => """
                {"id":"alert-1","sender_name":"NWS","event":"","start":1792670000,"end":1792678000,"description":"Flood risk","tags":["Flood"]}
                """,
            _ => throw new InvalidOperationException(request.AbsolutePath),
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openweathermap.org/") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new OpenWeather4TripWeatherProvider(client, cache, Options.Create(new OpenWeatherOptions
        {
            Enabled = true,
            ApiKey = "server-secret",
            EnableOfficialAlerts = true,
        }));

        TripWeatherSnapshot? result = await provider.GetAsync(
            new WeatherForecastRequest(16.0544m, 108.2022m,
                DateTimeOffset.FromUnixTimeSeconds(1792671300)), CancellationToken.None);

        result.Should().NotBeNull();
        result!.RainMmPerHour.Should().Be(12.5);
        result.Thunderstorm.Should().BeTrue();
        result.OfficialAlerts.Should().ContainSingle().Which.Label.Should().Be("Flood");
        handler.RequestUris.Should().OnlyContain(uri => !uri.Contains("server-secret", StringComparison.Ordinal));
        handler.AppIds.Should().OnlyContain(value => value == "server-secret");
    }

    [Fact]
    public async Task GetAsync_WhenFeatureIsDisabled_DoesNotCallProvider()
    {
        var handler = new WeatherHandler(_ => throw new InvalidOperationException("Must not be called."));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openweathermap.org/") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new OpenWeather4TripWeatherProvider(client, cache,
            Options.Create(new OpenWeatherOptions()));

        var result = await provider.GetAsync(
            new WeatherForecastRequest(16, 108, DateTimeOffset.UtcNow), CancellationToken.None);

        result.Should().BeNull();
        handler.RequestUris.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_WhenFifteenMinuteTimelineHasNoUsableRecord_FallsBackToHourly()
    {
        var planned = DateTimeOffset.FromUnixTimeSeconds(1792671300);
        var handler = new WeatherHandler(request => request.AbsolutePath switch
        {
            "/data/4.0/onecall/timeline/15min" => """{"data":[]}""",
            "/data/4.0/onecall/timeline/1h" =>
                $$"""{"data":[{"dt":{{planned.ToUnixTimeSeconds()}},"rain":{"1h":3.5},"weather":[],"alerts":[]}]}""",
            _ => throw new InvalidOperationException(request.AbsolutePath),
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openweathermap.org/") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new OpenWeather4TripWeatherProvider(client, cache, Options.Create(new OpenWeatherOptions
        {
            Enabled = true,
            ApiKey = "server-secret",
        }));

        var result = await provider.GetAsync(
            new WeatherForecastRequest(16, 108, planned), CancellationToken.None);

        result!.RainMmPerHour.Should().Be(3.5);
        handler.RequestUris.Should().ContainInOrder(
            "https://api.openweathermap.org/data/4.0/onecall/timeline/15min",
            "https://api.openweathermap.org/data/4.0/onecall/timeline/1h");
    }

    private sealed class WeatherHandler(Func<Uri, string> response) : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];
        public List<string?> AppIds { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            RequestUris.Add(uri.GetLeftPart(UriPartial.Path));
            AppIds.Add(ParseQuery(uri.Query).GetValueOrDefault("appid"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(uri), Encoding.UTF8, "application/json"),
            });
        }

        private static Dictionary<string, string> ParseQuery(string query) => query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]),
                part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : string.Empty);
    }
}
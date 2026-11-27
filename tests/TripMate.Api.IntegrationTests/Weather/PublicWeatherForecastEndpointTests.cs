using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.Common;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Weather.PublicForecast;

namespace TripMate.Api.IntegrationTests.Weather;

[Collection(nameof(TripMateApiFactory))]
public sealed class PublicWeatherForecastEndpointTests
{
    private const string Endpoint = "/api/v1/weather/forecast";

    [Fact]
    public async Task AnonymousRequestReturnsTheForecast_ForTheRoundedCell()
    {
        var provider = new StubProvider(Forecast());
        await using var factory = Factory(provider);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Endpoint}?lat=16.0549&lon=108.2025");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("api", body.RootElement.GetProperty("source").GetString());
        Assert.Equal(16.05m, body.RootElement.GetProperty("location").GetProperty("latitude").GetDecimal());
        Assert.True(body.RootElement.GetProperty("hourly").GetArrayLength() > 0);
        Assert.True(body.RootElement.GetProperty("current").TryGetProperty("observedAtUtc", out _));
        Assert.Equal((16.05m, 108.20m), Assert.Single(provider.Requested));
    }

    [Fact]
    public async Task CoordinatesOutsideVietnamAreRejected()
    {
        var provider = new StubProvider(Forecast());
        await using var factory = Factory(provider);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Endpoint}?lat=42.35&lon=-71.05");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(provider.Requested);
    }

    [Fact]
    public async Task ProviderOutageReturnsServiceUnavailable()
    {
        await using var factory = Factory(new StubProvider(null));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Endpoint}?lat=16.05&lon=108.2");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task RequestsOverThePerIpLimitAreThrottled()
    {
        await using var factory = Factory(new StubProvider(Forecast()));
        using var client = factory.CreateClient();

        HttpStatusCode last = HttpStatusCode.OK;
        for (var attempt = 0; attempt <= PublicWeatherRateLimiter.PermitLimit; attempt++)
        {
            last = (await client.GetAsync($"{Endpoint}?lat=16.05&lon=108.2")).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    private static TripMateApiFactory Factory(StubProvider provider) =>
        new(configureTestServices: services =>
        {
            services.RemoveAll<IPublicWeatherForecastProvider>();
            services.AddSingleton<IPublicWeatherForecastProvider>(provider);
        });

    private static PublicWeatherForecastData Forecast()
    {
        var now = DateTimeOffset.UtcNow;
        var thisHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var hours = Enumerable.Range(0, 30)
            .Select(offset => new PublicWeatherHourDto(thisHour.AddHours(offset), 29, 31, 15, 0, 7, 4.5, false, 1100,
                70, 38, 14.5))
            .ToArray();
        return new PublicWeatherForecastData(now, hours,
            [new PublicWeatherDayDto("2026-11-26", 23, 31, 33, 35, 0.4, 1.2, 8, 6.2, false, 1101, 74, "05:56", "17:18")]);
    }

    private sealed class StubProvider(PublicWeatherForecastData? data) : IPublicWeatherForecastProvider
    {
        public List<(decimal, decimal)> Requested { get; } = [];

        public Task<PublicWeatherForecastData?> GetAsync(decimal latitude, decimal longitude,
            CancellationToken cancellationToken)
        {
            lock (Requested)
            {
                Requested.Add((latitude, longitude));
            }

            return Task.FromResult(data);
        }
    }
}
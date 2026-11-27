using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.PublicForecast;

namespace TripMate.Application.UnitTests.Features.Weather.PublicForecast;

public sealed class GetPublicWeatherForecastQueryHandlerTests
{
    // 26/11/2026 10:07 local (UTC+7) is 03:07 UTC.
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 3, 7, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FetchedAt = new(2026, 11, 26, 2, 50, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_RoundsCoordinatesToTwoDecimals_BeforeAskingTheProvider()
    {
        var provider = new FakeProvider(Data(HoursFrom(Now.AddMinutes(-7), 3)));

        var result = await Handler(provider).Handle(new GetPublicWeatherForecastQuery(16.0549m, 108.2025m),
            CancellationToken.None);

        provider.Requested.Should().Equal((16.05m, 108.20m));
        result.Value.Location.Should().Be(new PublicWeatherLocationDto(16.05m, 108.20m, "Asia/Ho_Chi_Minh"));
    }

    [Fact]
    public async Task Handle_CurrentIsTheHourCoveringNow_AndHourlyStartsThere()
    {
        var thisHour = new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero);
        var provider = new FakeProvider(Data(HoursFrom(thisHour.AddHours(-2), 5)));

        var result = await Handler(provider).Handle(new GetPublicWeatherForecastQuery(16m, 108m), CancellationToken.None);

        var response = result.Value;
        response.Current.ObservedAtUtc.Should().Be(thisHour);
        response.Current.TemperatureC.Should().Be(TemperatureAt(thisHour));
        response.Hourly.Select(hour => hour.StartUtc).Should().Equal(thisHour, thisHour.AddHours(1), thisHour.AddHours(2));
    }

    [Fact]
    public async Task Handle_HourlyIsCappedAt24Hours()
    {
        var thisHour = new DateTimeOffset(2026, 11, 26, 3, 0, 0, TimeSpan.Zero);
        var provider = new FakeProvider(Data(HoursFrom(thisHour, 120)));

        var result = await Handler(provider).Handle(new GetPublicWeatherForecastQuery(16m, 108m), CancellationToken.None);

        result.Value.Hourly.Should().HaveCount(GetPublicWeatherForecastQueryHandler.HourlyPoints);
        GetPublicWeatherForecastQueryHandler.HourlyPoints.Should().Be(24);
    }

    [Fact]
    public async Task Handle_PassesDailyAndFetchTimeThrough_AndLabelsTheSource()
    {
        var day = Day("2026-11-26");
        var provider = new FakeProvider(Data(HoursFrom(Now, 2), day));

        var result = await Handler(provider).Handle(new GetPublicWeatherForecastQuery(16m, 108m), CancellationToken.None);

        var response = result.Value;
        response.Daily.Should().Equal(day);
        response.FetchedAtUtc.Should().Be(FetchedAt);
        response.Source.Should().Be("api");
        response.Provider.Should().Be("Tomorrow.io");
        response.TimeZoneId.Should().Be("Asia/Ho_Chi_Minh");
    }

    [Fact]
    public async Task Handle_WhenProviderHasNoForecast_ReturnsUnavailable()
    {
        var result = await Handler(new FakeProvider(null)).Handle(new GetPublicWeatherForecastQuery(16m, 108m),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PublicWeatherForecastErrors.Unavailable);
    }

    [Fact]
    public async Task Handle_WhenEveryHourIsInThePast_ReturnsUnavailable()
    {
        var provider = new FakeProvider(Data(HoursFrom(Now.AddHours(-10), 3)));

        var result = await Handler(provider).Handle(new GetPublicWeatherForecastQuery(16m, 108m), CancellationToken.None);

        result.ErrorCode.Should().Be(PublicWeatherForecastErrors.Unavailable);
    }

    [Theory]
    [InlineData(16.05, 108.2, true)]
    [InlineData(6, 102, true)]
    [InlineData(24, 118, true)]
    [InlineData(5.99, 108, false)]
    [InlineData(24.01, 108, false)]
    [InlineData(16, 101.99, false)]
    [InlineData(16, 118.01, false)]
    [InlineData(42.35, -71.05, false)]
    public void Validator_AcceptsOnlyTheVietnamBox(double latitude, double longitude, bool valid)
    {
        var result = new GetPublicWeatherForecastQueryValidator()
            .Validate(new GetPublicWeatherForecastQuery((decimal)latitude, (decimal)longitude));

        result.IsValid.Should().Be(valid);
    }

    private static GetPublicWeatherForecastQueryHandler Handler(FakeProvider provider) =>
        new(provider, new FixedClock(Now));

    private static PublicWeatherForecastData Data(IReadOnlyList<PublicWeatherHourDto> hours, params PublicWeatherDayDto[] days) =>
        new(FetchedAt, hours, days);

    private static PublicWeatherHourDto[] HoursFrom(DateTimeOffset start, int count)
    {
        var first = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(offset => first.AddHours(offset))
            .Select(hour => new PublicWeatherHourDto(hour, TemperatureAt(hour), 30, 15, 0, 7, 4.5, false, 1100, 70, 38, 14.5))
            .ToArray();
    }

    private static double TemperatureAt(DateTimeOffset hour) => 20 + hour.Hour;

    private static PublicWeatherDayDto Day(string date) =>
        new(date, 23, 31, 33, 35, 0.4, 1.2, 8, 6.2, false, 1101, 74, "05:56", "17:18");

    private sealed class FakeProvider(PublicWeatherForecastData? data) : IPublicWeatherForecastProvider
    {
        public List<(decimal Latitude, decimal Longitude)> Requested { get; } = [];

        public Task<PublicWeatherForecastData?> GetAsync(decimal latitude, decimal longitude,
            CancellationToken cancellationToken)
        {
            Requested.Add((latitude, longitude));
            return Task.FromResult(data);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
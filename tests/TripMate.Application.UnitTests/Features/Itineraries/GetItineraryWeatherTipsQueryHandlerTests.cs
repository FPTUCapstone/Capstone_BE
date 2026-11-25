using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Itineraries.WeatherTips;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class GetItineraryWeatherTipsQueryHandlerTests
{
    private const long TravelerId = 7;

    // 25/11/2026 09:00 local (UTC+7) is 02:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Morning = new(2026, 11, 25, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_ReturnsTipsForTheStopHours()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddHours(2)));
        var provider = new FakeDayForecastProvider().WithSeries(16.1m, 108.2m,
            Hours(Hour(Morning, uv: 8, probability: 70), Hour(Morning.AddHours(1), temperature: 19), Hour(Morning.AddHours(5), uv: 11)));

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        var tips = result.Value.WeatherTips!;
        tips.Date.Should().Be("2026-11-25");
        tips.TimeZoneId.Should().Be("Asia/Ho_Chi_Minh");
        tips.ForecastAtUtc.Should().Be(Now);
        tips.Tips.Select(tip => tip.Code).Should().Equal("weather_tip.rain", "weather_tip.uv", "weather_tip.cool");
        // The 12:00 UTC hour is outside the stop, so its UV 11 must not leak into the summary.
        tips.Summary.Should().Be(new WeatherTipsSummaryDto(19, 25, 70, 8));
    }

    [Fact]
    public async Task Handle_WhenOneStopHasNoForecastHour_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db,
            (16.05m, 108.20m, Morning, Morning.AddHours(1)),
            (16.06m, 108.21m, Morning.AddHours(3), Morning.AddHours(4)));
        var provider = new FakeDayForecastProvider().WithSeries(16.1m, 108.2m, Hours(Hour(Morning), Hour(Morning.AddHours(1))));

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WeatherTips.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenAStopEndsAfterTheLastForecastHour_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddMinutes(90)));
        var provider = new FakeDayForecastProvider().WithSeries(16.1m, 108.2m, Hours(Hour(Morning)));

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenForecastDisabled_ReturnsNullAndDoesNotCallProvider()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddHours(1)));
        var provider = new FakeDayForecastProvider();

        var result = await CreateHandler(db, itinerary, provider, enabled: false).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips.Should().BeNull();
        provider.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenProviderAnswersNothing_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddHours(1)));
        var provider = new FakeDayForecastProvider().WithSeries(16.1m, 108.2m, null);

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenProviderThrows_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddHours(1)));
        var provider = new FakeDayForecastProvider { Failure = new HttpRequestException("429") };

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.WeatherTips.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenProviderIsSlowerThanTheTimeout_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddHours(1)));
        var provider = new FakeDayForecastProvider { WaitForCancellation = true };

        var result = await CreateHandler(db, itinerary, provider, timeoutSeconds: 1).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenCallerCancels_Propagates()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddHours(1)));
        var provider = new FakeDayForecastProvider { WaitForCancellation = true };
        using var source = new CancellationTokenSource();
        source.Cancel();

        var act = () => CreateHandler(db, itinerary, provider).Handle(Query(itinerary), source.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_WhenEveryStopAlreadyEnded_ReturnsNullWithoutCallingProvider()
    {
        await using var db = CreateDbContext();
        var past = Now.AddDays(-2);
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, past, past.AddHours(1)));
        var provider = new FakeDayForecastProvider();

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips.Should().BeNull();
        provider.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenMoreThanThreeLocationCells_ReturnsNullWithoutCallingProvider()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db,
            (16.0m, 108.0m, Morning, Morning.AddMinutes(30)),
            (16.3m, 108.0m, Morning.AddHours(1), Morning.AddMinutes(90)),
            (16.6m, 108.0m, Morning.AddHours(2), Morning.AddMinutes(150)),
            (16.9m, 108.0m, Morning.AddHours(3), Morning.AddMinutes(210)));
        var provider = new FakeDayForecastProvider();

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips.Should().BeNull();
        provider.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_StopsInTheSameCell_CallProviderOnceWithTheCellCentre()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db,
            (16.04m, 108.21m, Morning, Morning.AddHours(1)),
            (16.01m, 108.19m, Morning.AddHours(2), Morning.AddHours(3)));
        var provider = new FakeDayForecastProvider().WithSeries(16.0m, 108.2m,
            Hours(Hour(Morning), Hour(Morning.AddHours(1)), Hour(Morning.AddHours(2))));

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        provider.Calls.Should().Equal((16.0m, 108.2m));
        result.Value.WeatherTips.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_RestItemsAndStopsWithoutTimes_AreIgnored()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddHours(1)));
        var tracked = await db.Itineraries.Include(item => item.Items).SingleAsync(item => item.Id == itinerary);
        tracked.AddItem(ItineraryItem.CreateRest(2, Morning.AddHours(8), Morning.AddHours(9), "Free time"));
        await db.SaveChangesAsync();
        var provider = new FakeDayForecastProvider().WithSeries(16.1m, 108.2m, Hours(Hour(Morning)));

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_ItineraryWithoutVisitStops_ReturnsNull()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db);
        var provider = new FakeDayForecastProvider();

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips.Should().BeNull();
        provider.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_PlanCrossingMidnight_UsesLocalDateOfFirstStop()
    {
        await using var db = CreateDbContext();
        // 23:30 local on 25/11 is 16:30 UTC; the plan runs on into 26/11 local.
        var first = new DateTimeOffset(2026, 11, 25, 16, 30, 0, TimeSpan.Zero);
        var itinerary = await SeedAsync(db,
            (16.05m, 108.20m, first, first.AddMinutes(30)),
            (16.06m, 108.21m, first.AddHours(2), first.AddHours(3)));
        var provider = new FakeDayForecastProvider().WithSeries(16.1m, 108.2m,
            Hours(Hour(first.AddMinutes(-30)), Hour(first.AddHours(2).AddMinutes(-30)), Hour(first.AddHours(3).AddMinutes(-30))));

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips!.Date.Should().Be("2026-11-25");
    }

    [Fact]
    public async Task Handle_CalmDay_ReturnsSummaryWithNoTips()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddHours(1)));
        var provider = new FakeDayForecastProvider().WithSeries(16.1m, 108.2m, Hours(Hour(Morning)));

        var result = await CreateHandler(db, itinerary, provider).Handle(Query(itinerary), CancellationToken.None);

        result.Value.WeatherTips!.Tips.Should().BeEmpty();
        result.Value.WeatherTips.Summary.MinTempC.Should().Be(25);
    }

    [Fact]
    public async Task Handle_OtherTravelersItinerary_PassesThroughNotFound()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedAsync(db, (16.05m, 108.20m, Morning, Morning.AddHours(1)));
        var provider = new FakeDayForecastProvider();
        var handler = new GetItineraryWeatherTipsQueryHandler(
            db, new StubAccessService(null), provider, new FixedClock(Now), Options(true, 3),
            NullLogger<GetItineraryWeatherTipsQueryHandler>.Instance);

        var result = await handler.Handle(new GetItineraryWeatherTipsQuery(itinerary, 999), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ItineraryErrorCodes.NotFound);
        provider.Calls.Should().BeEmpty();
    }

    private static TestDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static GetItineraryWeatherTipsQuery Query(long itineraryId) => new(itineraryId, TravelerId);

    private static GetItineraryWeatherTipsQueryHandler CreateHandler(
        TestDbContext db, long itineraryId, FakeDayForecastProvider provider, bool enabled = true, int timeoutSeconds = 3)
    {
        var itinerary = db.Itineraries.AsNoTracking().Single(item => item.Id == itineraryId);
        return new GetItineraryWeatherTipsQueryHandler(
            db, new StubAccessService(itinerary), provider, new FixedClock(Now), Options(enabled, timeoutSeconds),
            NullLogger<GetItineraryWeatherTipsQueryHandler>.Instance);
    }

    private static IOptions<WeatherTipsOptions> Options(bool enabled, int timeoutSeconds) =>
        Microsoft.Extensions.Options.Options.Create(new WeatherTipsOptions { Enabled = enabled, TimeoutSeconds = timeoutSeconds });

    private static async Task<long> SeedAsync(
        TestDbContext db,
        params (decimal Latitude, decimal Longitude, DateTimeOffset Arrival, DateTimeOffset Departure)[] stops)
    {
        var category = PoiCategory.Create("Weather", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(TravelerId, "Weather", Itinerary.ActiveStatus, Now);
        var sequence = 1;
        foreach (var stop in stops)
        {
            var poi = PointOfInterest.Create(category, $"POI {sequence}", stop.Latitude, stop.Longitude, TravelerId, Now);
            db.PointsOfInterest.Add(poi);
            await db.SaveChangesAsync();
            itinerary.AddItem(ItineraryItem.CreateVisit(sequence++, poi.Id, stop.Arrival, stop.Departure, false, 0m, null));
        }

        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        return itinerary.Id;
    }

    private static HourlyForecast Hour(
        DateTimeOffset start,
        double temperature = 25,
        double probability = 0,
        double uv = 0) =>
        new(start, temperature, temperature, probability, 0, uv, 0, false);

    private static IReadOnlyList<HourlyForecast> Hours(params HourlyForecast[] hours) => hours;

    private sealed class FakeDayForecastProvider : IDayForecastProvider
    {
        private readonly Dictionary<(decimal, decimal), IReadOnlyList<HourlyForecast>?> series = [];

        public List<(decimal Latitude, decimal Longitude)> Calls { get; } = [];

        public Exception? Failure { get; init; }

        public bool WaitForCancellation { get; init; }

        public FakeDayForecastProvider WithSeries(decimal latitude, decimal longitude, IReadOnlyList<HourlyForecast>? hours)
        {
            series[(latitude, longitude)] = hours;
            return this;
        }

        public async Task<IReadOnlyList<HourlyForecast>?> GetHourlyAsync(
            decimal latitude, decimal longitude, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add((latitude, longitude));
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return series.GetValueOrDefault((latitude, longitude));
        }
    }

    private sealed class StubAccessService(Itinerary? itinerary) : IItineraryAccessService
    {
        public Task<Result<ItineraryAccess>> ResolveCurrentAsync(
            long itineraryId, long travelerUserId, bool trackCurrent, CancellationToken cancellationToken) =>
            Task.FromResult(itinerary is null
                ? Result.Failure<ItineraryAccess>(ItineraryErrorCodes.NotFound, "Itinerary was not found.")
                : Result.Success(new ItineraryAccess(ItineraryDetailAccessKind.Owner, itineraryId, 0, itinerary, true)));
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
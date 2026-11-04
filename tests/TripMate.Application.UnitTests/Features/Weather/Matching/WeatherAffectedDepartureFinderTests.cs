using FluentAssertions;

using TripMate.Application.Features.Weather.Emergency;
using TripMate.Application.Features.Weather.Matching;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Weather.Matching;

public sealed class WeatherAffectedDepartureFinderTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private static WeatherPolygon CreateDaNangPolygon()
    {
        return new WeatherPolygon(
        [
            new WeatherPoint(16.00m, 108.15m),
            new WeatherPoint(16.00m, 108.35m),
            new WeatherPoint(16.20m, 108.35m),
            new WeatherPoint(16.20m, 108.15m),
            new WeatherPoint(16.00m, 108.15m),
        ]);
    }

    private static Tour CreateTestTour(long id, string title)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, id);
        typeof(Tour).GetProperty(nameof(Tour.Title))!.SetValue(tour, title);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        return tour;
    }

    private static PointOfInterest CreateTestPoi(long id, string name, decimal lat, decimal lon)
    {
        var category = PoiCategory.Create("Landmark", null);
        var poi = PointOfInterest.Create(category, name, lat, lon, 1, BaseTime, "Address", "Desc");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, id);
        return poi;
    }

    [Fact]
    public async Task FindAffectedDeparturesAsync_WhenMeetingPointInsidePolygon_ReturnsDeparture()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var impactLoader = new WeatherDepartureImpactLoader();
        var finder = new WeatherAffectedDepartureFinder(context, impactLoader);

        var tour = CreateTestTour(1, "Da Nang Tour");
        context.Tours.Add(tour);

        var schedule = TourSchedule.Create(
            BaseTime.AddHours(1),
            BaseTime.AddHours(5),
            "Da Nang",
            20,
            BaseTime,
            meetingLatitude: 16.05m,
            meetingLongitude: 108.20m); // Inside Da Nang polygon
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        var evt = new NormalizedWeatherEvent(
            Provider: "TomorrowIo",
            ProviderEventId: "evt-dn-1",
            UpdatedAtUtc: BaseTime,
            ValidFromUtc: BaseTime,
            ValidToUtc: BaseTime.AddHours(8),
            Areas: [CreateDaNangPolygon()],
            ReceivedAtUtc: BaseTime);

        var results = await finder.FindAffectedDeparturesAsync(evt, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Schedule.Id.Should().Be(schedule.Id);
        results[0].Impact.MeetingPoint.Should().NotBeNull();
        results[0].Impact.MeetingPoint!.Latitude.Should().Be(16.05m);
    }

    [Fact]
    public async Task FindAffectedDeparturesAsync_WhenMeetingPointOutsideButItineraryStopInside_ReturnsDeparture()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var impactLoader = new WeatherDepartureImpactLoader();
        var finder = new WeatherAffectedDepartureFinder(context, impactLoader);

        var tour = CreateTestTour(2, "Hue to Da Nang Tour");
        context.Tours.Add(tour);

        var poiInDaNang = CreateTestPoi(10, "Son Tra", 16.10m, 108.25m);
        context.PointsOfInterest.Add(poiInDaNang);
        await context.SaveChangesAsync();

        var item = TourItineraryItem.Create(dayNo: 1, sequenceNo: 1, poiId: poiInDaNang.Id, plannedTime: null, stayDurationMinutes: 60, notes: "Visit", isRequiredForWeather: true);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.TourId))!.SetValue(item, tour.Id);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Tour))!.SetValue(item, tour);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Poi))!.SetValue(item, poiInDaNang);
        context.TourItineraryItems.Add(item);
        var list = (List<TourItineraryItem>)typeof(Tour).GetField("_itineraryItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tour)!;
        list.Add(item);

        var schedule = TourSchedule.Create(
            BaseTime.AddHours(1),
            BaseTime.AddHours(5),
            "Hue Meeting",
            20,
            BaseTime,
            meetingLatitude: 16.46m,
            meetingLongitude: 107.59m); // Hue is outside Da Nang polygon!
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        var evt = new NormalizedWeatherEvent(
            Provider: "TomorrowIo",
            ProviderEventId: "evt-dn-2",
            UpdatedAtUtc: BaseTime,
            ValidFromUtc: BaseTime,
            ValidToUtc: BaseTime.AddHours(8),
            Areas: [CreateDaNangPolygon()],
            ReceivedAtUtc: BaseTime);

        var results = await finder.FindAffectedDeparturesAsync(evt, CancellationToken.None);

        results.Should().HaveCount(1);
        results[0].Schedule.Id.Should().Be(schedule.Id);
    }

    [Fact]
    public async Task FindAffectedDeparturesAsync_WhenCompletelyOutsidePolygon_ReturnsEmpty()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var impactLoader = new WeatherDepartureImpactLoader();
        var finder = new WeatherAffectedDepartureFinder(context, impactLoader);

        var tour = CreateTestTour(3, "Hanoi Tour");
        context.Tours.Add(tour);

        var schedule = TourSchedule.Create(
            BaseTime.AddHours(1),
            BaseTime.AddHours(5),
            "Hanoi",
            20,
            BaseTime,
            meetingLatitude: 21.02m,
            meetingLongitude: 105.83m); // Hanoi is far away
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        var evt = new NormalizedWeatherEvent(
            Provider: "TomorrowIo",
            ProviderEventId: "evt-dn-3",
            UpdatedAtUtc: BaseTime,
            ValidFromUtc: BaseTime,
            ValidToUtc: BaseTime.AddHours(8),
            Areas: [CreateDaNangPolygon()],
            ReceivedAtUtc: BaseTime);

        var results = await finder.FindAffectedDeparturesAsync(evt, CancellationToken.None);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task FindAffectedDeparturesAsync_WhenTimeDoesNotOverlap_ReturnsEmpty()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var impactLoader = new WeatherDepartureImpactLoader();
        var finder = new WeatherAffectedDepartureFinder(context, impactLoader);

        var tour = CreateTestTour(4, "Da Nang Tour Next Week");
        context.Tours.Add(tour);

        var schedule = TourSchedule.Create(
            BaseTime.AddDays(7), // Next week
            BaseTime.AddDays(7).AddHours(5),
            "Da Nang",
            20,
            BaseTime,
            meetingLatitude: 16.05m,
            meetingLongitude: 108.20m);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        var evt = new NormalizedWeatherEvent(
            Provider: "TomorrowIo",
            ProviderEventId: "evt-dn-4",
            UpdatedAtUtc: BaseTime,
            ValidFromUtc: BaseTime,
            ValidToUtc: BaseTime.AddHours(8),
            Areas: [CreateDaNangPolygon()],
            ReceivedAtUtc: BaseTime);

        var results = await finder.FindAffectedDeparturesAsync(evt, CancellationToken.None);

        results.Should().BeEmpty();
    }
}

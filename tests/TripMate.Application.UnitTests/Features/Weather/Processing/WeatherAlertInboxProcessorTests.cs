using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.Matching;
using TripMate.Application.Features.Weather.Processing;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Weather;
using TripMate.Tests.Shared;

namespace TripMate.Application.UnitTests.Features.Weather.Processing;

/// <summary>
/// The processor driven end to end with the documented Tomorrow.io payloads, the real
/// normalizer, finder and impact loader, against an in-memory TripMate database.
/// </summary>
public sealed class WeatherAlertInboxProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Departure = Now.AddHours(20);
    private const string MeetingLocationId = "loc-meeting";
    private const string StopLocationId = "loc-stop";

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingInboxStore : IWeatherAlertInboxStore
    {
        public int Processed { get; private set; }
        public string? LastError { get; private set; }

        public Task<IReadOnlyList<WeatherAlertInboxItem>> LeasePendingItemsAsync(int batchSize, TimeSpan leaseDuration, string leasedBy, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WeatherAlertInboxItem>>([]);

        public Task<bool> EnqueueIfNotExistsAsync(string provider, string providerNotificationId, string payload, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> MarkProcessedAsync(WeatherAlertInboxItem leasedItem, CancellationToken cancellationToken)
        {
            Processed++;
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(WeatherAlertInboxItem leasedItem, string errorMessage, CancellationToken cancellationToken)
        {
            LastError = errorMessage;
            return Task.FromResult(true);
        }
    }

    private static (WeatherAlertInboxProcessor Processor, RecordingInboxStore Store) Create(TestDbContext context)
    {
        var store = new RecordingInboxStore();
        var normalizer = new TomorrowAlertNormalizer(Options.Create(new TomorrowIoOptions { AlertId = TomorrowAlertFixtures.DocsAlertId }));
        var processor = new WeatherAlertInboxProcessor(
            context, [normalizer], store, new TestTimeProvider(Now), NullLogger<WeatherAlertInboxProcessor>.Instance,
            new WeatherAffectedDepartureFinder(context, new WeatherDepartureImpactLoader()),
            Options.Create(new WeatherAlertProcessingOptions { SafetyReviewAcknowledgementSlaMinutes = 30 }));
        return (processor, store);
    }

    private static Task Process(WeatherAlertInboxProcessor processor, string payload, string deliveryId = "delivery-1") =>
        processor.ProcessInboxItemAsync(
            WeatherAlertInboxItem.CreatePending("TomorrowIo", payload, deliveryId, Now), CancellationToken.None);

    [Theory]
    [InlineData("PRIOR")]
    [InlineData("START")]
    public async Task OfficialNotification_ForAMonitoredMeetingPoint_CreatesOneReviewAndNothingElseChanges(string type)
    {
        using var context = await SeedAsync();
        var (processor, store) = Create(context);

        await Process(processor, TomorrowAlertFixtures.Webhook(type, locationId: MeetingLocationId, startTime: Departure));

        var review = await context.WeatherSafetyReviews.SingleAsync();
        review.TourScheduleId.Should().Be(5);
        review.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        review.AcknowledgementDeadlineUtc.Should().Be(Now.AddMinutes(30));
        (await context.WeatherEvents.SingleAsync()).Severity.Should().Be(WeatherEvent.SeverityModerate);
        (await context.Notifications.CountAsync(n => n.Type == Notification.WeatherSafetyReviewCreatedType)).Should().Be(1);
        store.Processed.Should().Be(1);

        // Manual-only: the review is the only outcome; the departure itself is untouched.
        (await context.TourSchedules.SingleAsync()).Status.Should().Be(TourScheduleStatus.Scheduled);
    }

    [Fact]
    public async Task DepartureSharingTheMonitoredMeetingPointCell_GetsAReview_EvenWithSlightlyDifferentCoordinates()
    {
        // The Location is keyed by the meeting point rounded to 4 decimals (about 11 m). Another
        // departure meeting a few metres away shares that Location and must be matched by it.
        using var context = await SeedAsync();
        var schedule = await context.TourSchedules.SingleAsync();
        schedule.SetVerifiedMeetingCoordinates(16.054412m, 108.202171m);
        await context.SaveChangesAsync();
        var (processor, _) = Create(context);

        await Process(processor, TomorrowAlertFixtures.Webhook("START", locationId: MeetingLocationId, startTime: Departure));

        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task OfficialNotification_ForAMonitoredRequiredStop_CreatesAReview()
    {
        using var context = await SeedAsync();
        var (processor, _) = Create(context);

        await Process(processor, TomorrowAlertFixtures.Webhook("START", locationId: StopLocationId, startTime: Departure.AddHours(2)));

        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task PriorThenStartOfTheSameOccurrence_KeepOneEventAndOneReview()
    {
        using var context = await SeedAsync();
        var (processor, _) = Create(context);

        await Process(processor, TomorrowAlertFixtures.Webhook("PRIOR", locationId: MeetingLocationId, startTime: Departure), "d-prior");
        await Process(processor, TomorrowAlertFixtures.Webhook("START", locationId: MeetingLocationId, startTime: Departure), "d-start");

        (await context.WeatherEvents.CountAsync()).Should().Be(1);
        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DuplicateDeliveryOfTheSamePayload_CreatesNothingTwice()
    {
        using var context = await SeedAsync();
        var (processor, _) = Create(context);
        string payload = TomorrowAlertFixtures.Webhook("PRIOR", locationId: MeetingLocationId, startTime: Departure);

        await Process(processor, payload, "d-1");
        await Process(processor, payload, "d-1-retry");

        (await context.WeatherEvents.CountAsync()).Should().Be(1);
        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task EndAllClear_AddsNoEvidence_AndLeavesTheOpenReviewForTheOperator()
    {
        using var context = await SeedAsync();
        var (processor, store) = Create(context);
        await Process(processor, TomorrowAlertFixtures.Webhook("START", locationId: MeetingLocationId, startTime: Departure), "d-start");

        await Process(processor, TomorrowAlertFixtures.EndWebhook(locationId: MeetingLocationId, endTime: Departure.AddHours(3)), "d-end");

        (await context.WeatherEvents.CountAsync()).Should().Be(1);
        (await context.WeatherSafetyReviews.SingleAsync()).Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        store.Processed.Should().Be(2);
    }

    [Fact]
    public async Task MinimalPayloadWithoutOptionalFields_StillCreatesAReview()
    {
        using var context = await SeedAsync();
        var (processor, _) = Create(context);

        await Process(processor, TomorrowAlertFixtures.MinimalWebhook("START", locationId: MeetingLocationId, startTime: Departure));

        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task LocationTripMateDoesNotMonitor_IsIgnoredSafely()
    {
        using var context = await SeedAsync();
        var (processor, store) = Create(context);

        await Process(processor, TomorrowAlertFixtures.Webhook("START", locationId: "someone-elses-location", startTime: Departure));

        (await context.WeatherEvents.CountAsync()).Should().Be(0);
        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(0);
        store.Processed.Should().Be(1);
        store.LastError.Should().BeNull();
    }

    [Fact]
    public async Task RetiredLocation_IsIgnoredSafely()
    {
        using var context = await SeedAsync();
        var mapping = await context.WeatherLocationMappings.SingleAsync(m => m.TomorrowLocationId == MeetingLocationId);
        mapping.Deactivate(Now);
        await context.SaveChangesAsync();
        var (processor, _) = Create(context);

        await Process(processor, TomorrowAlertFixtures.Webhook("START", locationId: MeetingLocationId, startTime: Departure));

        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task NotificationFromAnotherAlert_IsIgnoredSafely()
    {
        using var context = await SeedAsync();
        var (processor, store) = Create(context);

        await Process(processor, TomorrowAlertFixtures.Webhook("START", alertId: "another-alert", locationId: MeetingLocationId, startTime: Departure));

        (await context.WeatherEvents.CountAsync()).Should().Be(0);
        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(0);
        store.Processed.Should().Be(1);
    }

    [Fact]
    public async Task ReconciledListItem_CreatesTheSameReviewAsTheWebhook()
    {
        using var context = await SeedAsync();
        var (processor, _) = Create(context);

        await Process(processor, TomorrowAlertFixtures.ListItem(locationId: MeetingLocationId, expectedTime: Departure), "61d2e5719550aa00085ac502");

        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AlertOutsideTheDepartureWindow_CreatesNoReview()
    {
        using var context = await SeedAsync();
        var (processor, _) = Create(context);

        await Process(processor, TomorrowAlertFixtures.Webhook("START", locationId: MeetingLocationId, startTime: Departure.AddDays(3)));

        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"unexpected":true}""")]
    [InlineData("""{"data":{"alert":{"id":"38f689d83c264eb0b084ba095f2ea743"},"notification":{"type":"START"},"event":{}}}""")]
    public async Task PayloadThatBreaksTheContract_IsMarkedFailedNotProcessed(string payload)
    {
        using var context = await SeedAsync();
        var (processor, store) = Create(context);

        await Process(processor, payload);

        store.LastError.Should().NotBeNullOrWhiteSpace();
        store.Processed.Should().Be(0);
        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(0);
    }

    private static async Task<TestDbContext> SeedAsync()
    {
        var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 1L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 2L);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);

        var poi = PointOfInterest.Create(PoiCategory.Create("Landmark", null), "Marble Mountains",
            16.003500m, 108.263900m, 1, Now, "Address", "Desc");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, 100L);
        var stop = TourItineraryItem.Create(1, 1, poi.Id, null, 120, "Visit", isRequiredForWeather: true);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.TourId))!.SetValue(stop, tour.Id);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Tour))!.SetValue(stop, tour);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Poi))!.SetValue(stop, poi);
        ((List<TourItineraryItem>)typeof(Tour).GetField("_itineraryItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tour)!).Add(stop);

        var schedule = TourSchedule.Create(Departure, Departure.AddHours(8), "Da Nang", 20, Now.AddDays(-5));
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 5L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        schedule.SetVerifiedMeetingCoordinates(16.054407m, 108.202167m);

        context.Tours.Add(tour);
        context.PointsOfInterest.Add(poi);
        context.TourItineraryItems.Add(stop);
        context.TourSchedules.Add(schedule);
        context.WeatherLocationMappings.AddRange(
            WeatherLocationMapping.Create("meeting-16.0544_108.2022", MeetingLocationId, 16.054407m, 108.202167m,
                "Meeting", schedule.EndAtUtc, Now.AddDays(-1)),
            WeatherLocationMapping.Create("poi-100", StopLocationId, 16.003500m, 108.263900m,
                "Marble Mountains", schedule.EndAtUtc, Now.AddDays(-1)));
        await context.SaveChangesAsync();
        return context;
    }
}
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

    private sealed class RecordingReviewNotifier : IWeatherSafetyReviewNotifier
    {
        public List<(long OperatorUserId, WeatherSafetyReviewChanged Change)> Calls { get; } = [];

        public Task NotifyChangedAsync(long operatorUserId, WeatherSafetyReviewChanged notification, CancellationToken cancellationToken)
        {
            Calls.Add((operatorUserId, notification));
            return Task.CompletedTask;
        }
    }

    private static (WeatherAlertInboxProcessor Processor, RecordingInboxStore Store, RecordingReviewNotifier Notifier) CreateWithNotifier(
        TestDbContext context,
        DateTimeOffset? nowUtc = null)
    {
        var store = new RecordingInboxStore();
        var notifier = new RecordingReviewNotifier();
        var normalizer = new TomorrowAlertNormalizer(Options.Create(new TomorrowIoOptions { AlertId = TomorrowAlertFixtures.DocsAlertId }));
        var processor = new WeatherAlertInboxProcessor(
            context, [normalizer], store, new TestTimeProvider(nowUtc ?? Now), NullLogger<WeatherAlertInboxProcessor>.Instance,
            new WeatherAffectedDepartureFinder(context, new WeatherDepartureImpactLoader()),
            Options.Create(new WeatherAlertProcessingOptions { SafetyReviewAcknowledgementSlaMinutes = 30 }),
            notifier);
        return (processor, store, notifier);
    }

    private static (WeatherAlertInboxProcessor Processor, RecordingInboxStore Store) Create(TestDbContext context)
    {
        var (processor, store, _) = CreateWithNotifier(context);
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
    public async Task NewReview_WarnsEveryTravelerHoldingAnOpenBooking_InAppOnly_AndChangesNoBooking()
    {
        using var context = await SeedAsync();
        context.Bookings.AddRange(
            Booking.Restore(10, 3, 5, 2, 200_000m, BookingStatus.Confirmed, PaymentStatus.Paid,
                Booking.DefaultCancellationPolicySnapshot),
            Booking.Restore(11, 4, 5, 1, 100_000m, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
                Booking.DefaultCancellationPolicySnapshot),
            Booking.Restore(12, 6, 5, 1, 100_000m, BookingStatus.Cancelled, PaymentStatus.Paid,
                Booking.DefaultCancellationPolicySnapshot));
        await context.SaveChangesAsync();
        var (processor, _) = Create(context);
        string payload = TomorrowAlertFixtures.Webhook("START", locationId: MeetingLocationId, startTime: Departure);

        await Process(processor, payload, "d-1");
        await Process(processor, payload, "d-2");

        List<Notification> warnings = await context.Notifications
            .Where(notice => notice.Type == Notification.WeatherWarningType)
            .OrderBy(notice => notice.UserId)
            .ToListAsync();
        warnings.Select(notice => (notice.UserId, notice.RelatedEntityId))
            .Should().Equal((3L, (long?)10L), (4L, (long?)11L));
        warnings.Should().OnlyContain(notice => notice.Channel == NotificationChannel.InApp
            && notice.RelatedEntityType == Notification.BookingRelatedEntityType
            && notice.Title == Notification.WeatherWarningTitle
            && notice.Body == Notification.WeatherWarningBody);
        context.AlertDeliveryWork.Should().NotContain(work =>
            warnings.Select(notice => notice.Id).Contains(work.NotificationId));
        (await context.Bookings.OrderBy(booking => booking.Id).Select(booking => booking.Status).ToListAsync())
            .Should().Equal(BookingStatus.Confirmed, BookingStatus.PendingPayment, BookingStatus.Cancelled);
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
    public async Task EndAllClear_MarksExistingEventWithdrawn_NotifiesOperator_AndLeavesTheOpenReviewForTheOperator()
    {
        using var context = await SeedAsync();
        var (processor, store, notifier) = CreateWithNotifier(context);
        await Process(processor, TomorrowAlertFixtures.Webhook("START", locationId: MeetingLocationId, startTime: Departure), "d-start");

        await Process(processor, TomorrowAlertFixtures.EndWebhook(locationId: MeetingLocationId, endTime: Departure.AddHours(3)), "d-end");

        var weatherEvent = await context.WeatherEvents.SingleAsync();
        weatherEvent.IsWithdrawn.Should().BeTrue();
        var review = await context.WeatherSafetyReviews.SingleAsync();
        review.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        notifier.Calls.Select(c => c.Change.ChangeType).Should().Equal(
            WeatherSafetyReviewChangeTypes.Created,
            WeatherSafetyReviewChangeTypes.Updated);
        store.Processed.Should().Be(2);
    }

    [Fact]
    public async Task EndAllClear_WithoutPrecedingEvent_AddsNoEvidenceAndNoReview()
    {
        using var context = await SeedAsync();
        var (processor, store) = Create(context);

        await Process(processor, TomorrowAlertFixtures.EndWebhook(locationId: MeetingLocationId, endTime: Departure.AddHours(1)), "d-end-only");

        (await context.WeatherEvents.CountAsync()).Should().Be(0);
        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(0);
        store.Processed.Should().Be(1);
    }

    [Fact]
    public async Task MultiStageCheckpoints_ReopenAcknowledgedReviewForOperator_WithoutRewarningTravelers()
    {
        using var context = await SeedAsync();
        var startAt = Now.AddHours(50);
        var schedule = await context.TourSchedules.SingleAsync();
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.StartAtUtc))!.SetValue(schedule, startAt);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.EndAtUtc))!.SetValue(schedule, startAt.AddHours(8));
        context.Bookings.Add(Booking.Restore(10, 3, 5, 2, 200_000m, BookingStatus.Confirmed, PaymentStatus.Paid,
            Booking.DefaultCancellationPolicySnapshot));
        await context.SaveChangesAsync();

        // Stage 1: 48h prior
        var t48h = Now;
        var (p48, _, n48) = CreateWithNotifier(context, t48h);
        await p48.ProcessInboxItemAsync(
            WeatherAlertInboxItem.CreatePending("TomorrowIo",
                TomorrowAlertFixtures.Webhook("PRIOR", locationId: MeetingLocationId, startTime: startAt, priorMinutes: 2880),
                "d-48h", t48h),
            CancellationToken.None);

        var review = await context.WeatherSafetyReviews.SingleAsync();
        review.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        review.Acknowledge(2L, t48h.AddMinutes(10), "Theo dõi thêm dự báo ngày mai").Should().BeTrue();
        await context.SaveChangesAsync();

        // Late reconciliation of the already-acknowledged 48h notice must NOT reopen
        var reconciled48h = TomorrowAlertFixtures.ListItem("notif-48h", locationId: MeetingLocationId, expectedTime: startAt)
            .Replace("2022-01-03T12:00:49.930Z", t48h.ToString("O"), StringComparison.Ordinal);
        var (pRecon, _, nRecon) = CreateWithNotifier(context, t48h.AddMinutes(15));
        await pRecon.ProcessInboxItemAsync(
            WeatherAlertInboxItem.CreatePending("TomorrowIo", reconciled48h, "notif-48h", t48h.AddMinutes(15)),
            CancellationToken.None);
        (await context.WeatherSafetyReviews.SingleAsync()).Status.Should().Be(WeatherSafetyReview.StatusAcknowledged);
        nRecon.Calls.Should().BeEmpty();

        // Subsequent stages: 24h (1440), 12h (720), 6h (360), START (0)
        (string Type, int Minutes, int HoursElapsed)[] stages =
        [
            ("PRIOR", 1440, 24),
            ("PRIOR", 720, 36),
            ("PRIOR", 360, 42),
            ("START", 0, 48),
        ];

        foreach (var (type, minutes, hoursElapsed) in stages)
        {
            var stageNow = Now.AddHours(hoursElapsed);
            var (stageProcessor, _, stageNotifier) = CreateWithNotifier(context, stageNow);
            await stageProcessor.ProcessInboxItemAsync(
                WeatherAlertInboxItem.CreatePending("TomorrowIo",
                    TomorrowAlertFixtures.Webhook(type, locationId: MeetingLocationId, startTime: startAt, priorMinutes: minutes),
                    $"d-{type}-{minutes}", stageNow),
                CancellationToken.None);

            var reopened = await context.WeatherSafetyReviews.SingleAsync();
            reopened.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement, $"stage {type}:{minutes} must re-alert Operator");
            reopened.AcknowledgementDeadlineUtc.Should().Be(stageNow.AddMinutes(30));
            reopened.ImmediateInstructions.Should().Be("Theo dõi thêm dự báo ngày mai");
            stageNotifier.Calls.Should().ContainSingle(c => c.Change.ChangeType == WeatherSafetyReviewChangeTypes.Updated);

            reopened.Acknowledge(2L, stageNow.AddMinutes(5)).Should().BeTrue();
            await context.SaveChangesAsync();
        }

        // Still one WeatherEvent, one WeatherSafetyReview, 5 Operator push notices (initial + 4 stages), and ONLY 1 Traveler warning!
        (await context.WeatherEvents.CountAsync()).Should().Be(1);
        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(1);
        (await context.Notifications.CountAsync(n => n.Type == Notification.WeatherSafetyReviewCreatedType)).Should().Be(5);
        (await context.Notifications.CountAsync(n => n.Type == Notification.WeatherWarningType)).Should().Be(1);
    }

    [Fact]
    public async Task StopWithPlannedTime_IgnoresWeatherOutsideStopStayWindow_AndMatchesOverlapDuringStay()
    {
        using var context = await SeedAsync();
        // Schedule: 2026-11-03 08:00..17:00 ICT (01:00..10:00 UTC). Stop 100 visited 09:00..11:00 ICT (02:00..04:00 UTC).
        var scheduleStartUtc = new DateTimeOffset(2026, 11, 3, 1, 0, 0, TimeSpan.Zero);
        var schedule = await context.TourSchedules.SingleAsync();
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.StartAtUtc))!.SetValue(schedule, scheduleStartUtc);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.EndAtUtc))!.SetValue(schedule, scheduleStartUtc.AddHours(9));
        var stop = await context.TourItineraryItems.SingleAsync();
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.PlannedTime))!.SetValue(stop, new TimeOnly(9, 0));
        await context.SaveChangesAsync();

        var (processor, _) = Create(context);

        // Weather at Stop 100 starting at 15:00 ICT (08:00 UTC..+24h) is AFTER the 09:00..11:00 ICT visit -> no review!
        await Process(processor, TomorrowAlertFixtures.Webhook(
            "PRIOR", locationId: StopLocationId, startTime: new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero), priorMinutes: 1440), "d-late");
        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(0);

        // Weather starting at 10:00 ICT (03:00 UTC) overlaps the 09:00..11:00 ICT stay -> creates review!
        string overlapAlert = """
            {"data":{"alert":{"id":"38f689d83c264eb0b084ba095f2ea743"},"insight":{"id":"rain"},
            "locations":[{"id":"loc-stop"}],
            "notification":{"type":"PUBLISH","title":"Heavy Rain","description":"Heavy rain at stop."},
            "event":{"startTime":"2026-11-03T03:00:00Z","endTime":"2026-11-03T05:00:00Z","updateTime":"2026-11-02T10:00:00Z",
              "severity":"severe","source":"NCHMF",
              "eventValues":{"origin":"NCHMF","title":"Mưa lớn","description":"Rủi ro thiên tai cấp 3",
                "location":{"type":"Polygon","coordinates":[[[108.20,15.95],[108.30,15.95],[108.30,16.05],[108.20,16.05],[108.20,15.95]]]}}}}}
            """;
        await Process(processor, overlapAlert, "d-overlap");
        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ActiveTour_SkipsStopAlreadyPassedInTripSessionNavigation()
    {
        using var context = await SeedAsync();
        // Active tour: started 2 hours ago, ends in 6 hours.
        var activeStartUtc = Now.AddHours(-2);
        var schedule = await context.TourSchedules.SingleAsync();
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.StartAtUtc))!.SetValue(schedule, activeStartUtc);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.EndAtUtc))!.SetValue(schedule, Now.AddHours(6));

        var poi2 = PointOfInterest.Create(PoiCategory.Create("Town", null), "Hoi An",
            15.880100m, 108.338000m, 1, Now, "Address", "Desc");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi2, 101L);
        context.PointsOfInterest.Add(poi2);

        var booking = Booking.Restore(50, 3, schedule.Id, 2, 200_000m, BookingStatus.Confirmed, PaymentStatus.Paid,
            Booking.DefaultCancellationPolicySnapshot);
        context.Bookings.Add(booking);

        var itinerary = (Itinerary)Activator.CreateInstance(typeof(Itinerary), nonPublic: true)!;
        typeof(Itinerary).GetProperty(nameof(Itinerary.Id))!.SetValue(itinerary, 500L);
        typeof(Itinerary).GetProperty(nameof(Itinerary.TravelerUserId))!.SetValue(itinerary, 3L);
        typeof(Itinerary).GetProperty(nameof(Itinerary.SourceBookingId))!.SetValue(itinerary, booking.Id);
        typeof(Itinerary).GetProperty(nameof(Itinerary.SourceTourId))!.SetValue(itinerary, 1L);
        context.Itineraries.Add(itinerary);

        var navStop1 = TripSessionItem.Snapshot(1001, 1, 100L, "Marble Mountains", 16.003500m, 108.263900m,
            activeStartUtc.AddMinutes(15), activeStartUtc.AddMinutes(75), true);
        typeof(TripSessionItem).GetProperty(nameof(TripSessionItem.ReachedAtUtc))!.SetValue(navStop1, activeStartUtc.AddMinutes(20));
        var navStop2 = TripSessionItem.Snapshot(1002, 2, 101L, "Hoi An", 15.880100m, 108.338000m,
            Now.AddHours(1), Now.AddHours(3), true);

        // Session is Navigating to Stop 2 (no longer Exploring Stop 1 -> Stop 1 is Passed!)
        var session = TripSession.Start(itinerary.Id, itinerary.Id, 3L, Guid.NewGuid(), activeStartUtc, Now.AddHours(6),
            [navStop1, navStop2]);
        typeof(TripSession).GetProperty(nameof(TripSession.Itinerary))!.SetValue(session, itinerary);
        context.TripSessions.Add(session);
        await context.SaveChangesAsync();

        var (processor, _) = Create(context);
        await Process(processor, TomorrowAlertFixtures.Webhook("START", locationId: StopLocationId, startTime: Now), "d-passed-stop");

        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(0);
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
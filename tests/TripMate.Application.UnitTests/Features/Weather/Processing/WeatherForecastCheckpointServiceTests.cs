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

namespace TripMate.Application.UnitTests.Features.Weather.Processing;

public sealed class WeatherForecastCheckpointServiceTests
{
    // Sunday 2026-11-29 08:00 VN (UTC+7) = 2026-11-29 01:00 UTC.
    private static readonly DateTimeOffset SundayDepartureUtc = new(2026, 11, 29, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PriorReceivedUtc = SundayDepartureUtc.AddHours(-48);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class StubTomorrowForecastClient(
        Func<double, double, TomorrowHourlyForecastResponse> responder) : ITomorrowAlertsClient
    {
        public List<(double Latitude, double Longitude)> Calls { get; } = [];

        public Task<TomorrowNotificationsResponse> GetNotificationsAsync(DateTimeOffset startTime, DateTimeOffset endTime, string? pageToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TomorrowNotificationsResponse([], null));

        public Task<TomorrowCreatedLocation> CreateLocationAsync(string name, double latitude, double longitude, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TomorrowCreatedLocation(TomorrowCallOutcome.Skipped, null));

        public Task<TomorrowCallOutcome> DeleteLocationAsync(string locationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(TomorrowCallOutcome.Skipped);

        public Task<TomorrowLinkedLocations> GetLinkedLocationIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TomorrowLinkedLocations(TomorrowCallOutcome.Skipped, new HashSet<string>()));

        public Task<TomorrowCallOutcome> LinkLocationsAsync(IReadOnlyCollection<string> locationIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(TomorrowCallOutcome.Skipped);

        public Task<TomorrowCallOutcome> UnlinkLocationsAsync(IReadOnlyCollection<string> locationIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(TomorrowCallOutcome.Skipped);

        public Task<TomorrowHourlyForecastResponse> GetHourlyForecastAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
        {
            Calls.Add((latitude, longitude));
            return Task.FromResult(responder(latitude, longitude));
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

    private static TomorrowHourlyForecastSlot SafeSlot(DateTimeOffset timeUtc) =>
        new(timeUtc, RainIntensityMmPerHour: 0.4, WindSpeedMetersPerSecond: 2.5, WindGustMetersPerSecond: 4.0, VisibilityKm: 10.0, WeatherCode: 1001);

    private static TomorrowHourlyForecastSlot HeavyRainSlot(DateTimeOffset timeUtc, double rainMmPerHour = 14.2) =>
        new(timeUtc, RainIntensityMmPerHour: rainMmPerHour, WindSpeedMetersPerSecond: 4.0, WindGustMetersPerSecond: 7.0, VisibilityKm: 4.0, WeatherCode: 4201);

    private static TomorrowHourlyForecastSlot DenseFogSlot(DateTimeOffset timeUtc) =>
        new(timeUtc, RainIntensityMmPerHour: 0.0, WindSpeedMetersPerSecond: 1.0, WindGustMetersPerSecond: 2.0, VisibilityKm: 0.2, WeatherCode: 2000);

    private static TomorrowHourlyForecastSlot ThunderstormSlot(DateTimeOffset timeUtc) =>
        new(timeUtc, RainIntensityMmPerHour: 3.0, WindSpeedMetersPerSecond: 5.0, WindGustMetersPerSecond: 8.0, VisibilityKm: 6.0, WeatherCode: 8000);

    private static (WeatherForecastCheckpointService Service, MutableTimeProvider Clock, StubTomorrowForecastClient Client, RecordingReviewNotifier Notifier) Create(
        TestDbContext context,
        DateTimeOffset nowUtc,
        Func<double, double, TomorrowHourlyForecastResponse> responder,
        WeatherAlertProcessingOptions? options = null)
    {
        var clock = new MutableTimeProvider(nowUtc);
        var client = new StubTomorrowForecastClient(responder);
        var notifier = new RecordingReviewNotifier();
        var service = new WeatherForecastCheckpointService(
            context,
            client,
            new WeatherDepartureImpactLoader(),
            clock,
            Options.Create(options ?? new WeatherAlertProcessingOptions { Enabled = true, SafetyReviewAcknowledgementSlaMinutes = 30 }),
            NullLogger<WeatherForecastCheckpointService>.Instance,
            notifier);
        return (service, clock, client, notifier);
    }

    [Fact]
    public async Task ForecastClearedAt12hCheckpoint_MarksEventWithdrawnAndNotifiesOperatorWithoutClosingReview()
    {
        using var context = await SeedSingleTourAsync(acknowledgeReview: true);
        // Saturday 20:05 VN = 11h55m before Sunday 08:00 VN departure (crossed 12h checkpoint).
        DateTimeOffset saturdayNightUtc = SundayDepartureUtc.AddHours(-12).AddMinutes(5);
        var (service, clock, client, notifier) = Create(
            context,
            saturdayNightUtc,
            (_, _) => new TomorrowHourlyForecastResponse(
                TomorrowCallOutcome.Success,
                [
                    SafeSlot(SundayDepartureUtc),
                    SafeSlot(SundayDepartureUtc.AddHours(1)),
                    SafeSlot(SundayDepartureUtc.AddHours(2)),
                    SafeSlot(SundayDepartureUtc.AddHours(3)),
                ]));

        int updated = await service.EvaluateDueCheckpointsAsync();

        updated.Should().Be(1);
        var weatherEvent = await context.WeatherEvents.SingleAsync();
        weatherEvent.IsWithdrawn.Should().BeTrue();
        weatherEvent.UpdatedAtUtc.Should().Be(saturdayNightUtc);

        var review = await context.WeatherSafetyReviews.SingleAsync();
        review.Status.Should().Be(WeatherSafetyReview.StatusAcknowledged);

        notifier.Calls.Should().ContainSingle(c =>
            c.OperatorUserId == 2L
            && c.Change.ReviewId == review.Id
            && c.Change.ChangeType == WeatherSafetyReviewChangeTypes.Updated);
        (await context.Notifications.CountAsync()).Should().Be(0);

        // Next sweep 15 minutes later sees event is already withdrawn and makes zero HTTP calls.
        int callsAfterFirst = client.Calls.Count;
        clock.Current = saturdayNightUtc.AddMinutes(15);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Count.Should().Be(callsAfterFirst);
    }

    [Fact]
    public async Task ForecastStillHazardousAt24hCheckpoint_ReopensAcknowledgedReviewOncePerCheckpointWithoutDuplicateTravelerWarning()
    {
        using var context = await SeedSingleTourAsync(
            acknowledgeReview: true,
            operatorInstructions: "Theo dõi thêm đến sát giờ");
        DateTimeOffset satMorningUtc = SundayDepartureUtc.AddHours(-24).AddMinutes(5);
        var (service, clock, client, notifier) = Create(
            context,
            satMorningUtc,
            (_, _) => new TomorrowHourlyForecastResponse(
                TomorrowCallOutcome.Success,
                [
                    SafeSlot(SundayDepartureUtc),
                    HeavyRainSlot(SundayDepartureUtc.AddHours(1)), // 09:00 VN stop visit
                    SafeSlot(SundayDepartureUtc.AddHours(2)),
                ]));

        int updated = await service.EvaluateDueCheckpointsAsync();

        updated.Should().Be(1);
        var review = await context.WeatherSafetyReviews.SingleAsync();
        review.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        review.UrgentNoticeSentAtUtc.Should().Be(satMorningUtc);
        review.AcknowledgementDeadlineUtc.Should().Be(satMorningUtc.AddMinutes(30));
        review.ImmediateInstructions.Should().Be("Theo dõi thêm đến sát giờ");

        var weatherEvent = await context.WeatherEvents.SingleAsync();
        weatherEvent.IsWithdrawn.Should().BeFalse();
        weatherEvent.UpdatedAtUtc.Should().Be(satMorningUtc);

        // Operator push queued; no Traveler WeatherWarning created.
        var notices = await context.Notifications.ToListAsync();
        notices.Should().ContainSingle(n =>
            n.UserId == 2L
            && n.Type == Notification.WeatherSafetyReviewCreatedType
            && n.RelatedEntityId == review.Id);
        notices.Should().NotContain(n => n.Type == Notification.WeatherWarningType);
        notifier.Calls.Should().ContainSingle(c => c.Change.ChangeType == WeatherSafetyReviewChangeTypes.Updated);

        // 15 minutes later (still inside 24h checkpoint): zero API calls, zero duplicate push.
        int callsAt24h = client.Calls.Count;
        clock.Current = satMorningUtc.AddMinutes(15);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Count.Should().Be(callsAt24h);

        // Operator acknowledges again; at 12h checkpoint, thunderstorm triggers reopen again.
        review.Acknowledge(2L, satMorningUtc.AddMinutes(20));
        await context.SaveChangesAsync();

        clock.Current = SundayDepartureUtc.AddHours(-12).AddMinutes(5);
        int updatedAt12h = await service.EvaluateDueCheckpointsAsync();
        updatedAt12h.Should().Be(1);
        (await context.WeatherSafetyReviews.SingleAsync()).Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
    }

    [Fact]
    public async Task HazardOutsideStopStayWindow_DoesNotKeepAlertActive()
    {
        // Early-morning dense fog at 02:00 VN (visibility 0.2 km) should NOT keep an 08:00-16:00 VN tour alerted.
        using var context = await SeedSingleTourAsync(acknowledgeReview: false);
        DateTimeOffset satNightUtc = SundayDepartureUtc.AddHours(-12).AddMinutes(2);
        var (service, _, _, _) = Create(
            context,
            satNightUtc,
            (_, _) => new TomorrowHourlyForecastResponse(
                TomorrowCallOutcome.Success,
                [
                    DenseFogSlot(SundayDepartureUtc.AddHours(-6)), // 02:00 VN (before tour)
                    SafeSlot(SundayDepartureUtc),                  // 08:00 VN meeting point
                    SafeSlot(SundayDepartureUtc.AddHours(1)),      // 09:00 VN stop
                    SafeSlot(SundayDepartureUtc.AddHours(2)),      // 10:00 VN stop
                    DenseFogSlot(SundayDepartureUtc.AddHours(10)), // 18:00 VN (after tour)
                ]));

        int updated = await service.EvaluateDueCheckpointsAsync();

        updated.Should().Be(1);
        (await context.WeatherEvents.SingleAsync()).IsWithdrawn.Should().BeTrue();
    }

    [Fact]
    public async Task ThunderstormCodeDuringMeetingTime_KeepsEventActive()
    {
        using var context = await SeedSingleTourAsync(acknowledgeReview: true);
        DateTimeOffset satNightUtc = SundayDepartureUtc.AddHours(-6).AddMinutes(1);
        var (service, _, _, _) = Create(
            context,
            satNightUtc,
            (_, _) => new TomorrowHourlyForecastResponse(
                TomorrowCallOutcome.Success,
                [
                    ThunderstormSlot(SundayDepartureUtc),
                    SafeSlot(SundayDepartureUtc.AddHours(1)),
                    SafeSlot(SundayDepartureUtc.AddHours(2)),
                ]));

        int updated = await service.EvaluateDueCheckpointsAsync();

        updated.Should().Be(1);
        (await context.WeatherEvents.SingleAsync()).IsWithdrawn.Should().BeFalse();
        (await context.WeatherSafetyReviews.SingleAsync()).Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
    }

    [Fact]
    public async Task ProviderUnavailableOrMissingOverlap_FailsSafeAndRetriesNextCycle()
    {
        using var context = await SeedSingleTourAsync(acknowledgeReview: true);
        DateTimeOffset satMorningUtc = SundayDepartureUtc.AddHours(-24).AddMinutes(5);
        bool returnUnavailable = true;
        var (service, clock, client, _) = Create(
            context,
            satMorningUtc,
            (_, _) => returnUnavailable
                ? new TomorrowHourlyForecastResponse(TomorrowCallOutcome.Unavailable, [])
                : new TomorrowHourlyForecastResponse(
                    TomorrowCallOutcome.Success,
                    [SafeSlot(SundayDepartureUtc.AddDays(2))])); // does not cover Sunday departure

        // First cycle: 429/5xx -> stops sweep on first point, leaves state untouched.
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Count.Should().Be(1);
        var evt = await context.WeatherEvents.SingleAsync();
        evt.IsWithdrawn.Should().BeFalse();
        evt.UpdatedAtUtc.Should().Be(PriorReceivedUtc);

        // Second cycle: forecast returns hours outside tour window -> still fail-safe (inconclusive).
        returnUnavailable = false;
        clock.Current = satMorningUtc.AddMinutes(15);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        evt = await context.WeatherEvents.SingleAsync();
        evt.IsWithdrawn.Should().BeFalse();
        evt.UpdatedAtUtc.Should().Be(PriorReceivedUtc);
    }

    [Fact]
    public async Task SharedWeatherEventAcrossTwoTours_DeduplicatesCellForecastAndDoesNotWithdrawWhileOtherTourStillImpacted()
    {
        using var context = await SeedTwoToursSharingEventAsync();
        // Tour 1 departs Sunday 08:00 VN (meeting + stop at 09:00-10:00 VN -> SAFE)
        // Tour 2 departs Sunday 10:00 VN (meeting + stop at 11:00-12:00 VN -> HEAVY RAIN at 11:00 VN)
        DateTimeOffset satMorningUtc = SundayDepartureUtc.AddHours(-24).AddMinutes(5);
        var (service, _, client, _) = Create(
            context,
            satMorningUtc,
            (_, _) => new TomorrowHourlyForecastResponse(
                TomorrowCallOutcome.Success,
                [
                    SafeSlot(SundayDepartureUtc),                  // 08:00 VN
                    SafeSlot(SundayDepartureUtc.AddHours(1)),      // 09:00 VN
                    SafeSlot(SundayDepartureUtc.AddHours(2)),      // 10:00 VN
                    HeavyRainSlot(SundayDepartureUtc.AddHours(3)), // 11:00 VN (Tour 2 stop!)
                    SafeSlot(SundayDepartureUtc.AddHours(4)),      // 12:00 VN
                ]));

        await service.EvaluateDueCheckpointsAsync();

        // Both tours use the same single MonitoringCell -> exactly 1 API call!
        client.Calls.Should().ContainSingle();
        // Shared WeatherEvent MUST stay active because Tour 2 is still rained on at 11:00 VN.
        (await context.WeatherEvents.SingleAsync()).IsWithdrawn.Should().BeFalse();
    }

    [Fact]
    public async Task NonCustomInsightOrBeforeFirstCheckpointOrRecentlyCreatedReview_MakesZeroCalls()
    {
        using var context = await SeedSingleTourAsync(acknowledgeReview: true, eventType: "SevereWeather");
        DateTimeOffset satNightUtc = SundayDepartureUtc.AddHours(-12).AddMinutes(5);
        var (service, _, client, _) = Create(
            context,
            satNightUtc,
            (_, _) => new TomorrowHourlyForecastResponse(TomorrowCallOutcome.Success, [SafeSlot(SundayDepartureUtc)]));

        // Government / non-CustomInsight alert is never overridden by point forecast.
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Should().BeEmpty();

        // Now test CustomInsight BEFORE first checkpoint (36h before departure).
        using var earlyContext = await SeedSingleTourAsync(acknowledgeReview: true, eventType: "CustomInsight");
        var (earlyService, _, earlyClient, _) = Create(
            earlyContext,
            SundayDepartureUtc.AddHours(-36),
            (_, _) => new TomorrowHourlyForecastResponse(TomorrowCallOutcome.Success, [SafeSlot(SundayDepartureUtc)]));

        (await earlyService.EvaluateDueCheckpointsAsync()).Should().Be(0);
        earlyClient.Calls.Should().BeEmpty();

        // Now test CustomInsight created at T-18h (after 24h checkpoint, before 12h checkpoint).
        using var recentContext = await SeedSingleTourAsync(
            acknowledgeReview: true,
            eventType: "CustomInsight",
            createdAtUtc: SundayDepartureUtc.AddHours(-18));
        var (recentService, _, recentClient, _) = Create(
            recentContext,
            SundayDepartureUtc.AddHours(-17),
            (_, _) => new TomorrowHourlyForecastResponse(TomorrowCallOutcome.Success, [SafeSlot(SundayDepartureUtc)]));

        (await recentService.EvaluateDueCheckpointsAsync()).Should().Be(0);
        recentClient.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingOrEscalatedReviewWithPersistingHazard_AdvancesEventTimestampWithoutDuplicatePush()
    {
        using var context = await SeedSingleTourAsync(acknowledgeReview: false);
        DateTimeOffset satMorningUtc = SundayDepartureUtc.AddHours(-24).AddMinutes(5);
        var (service, clock, client, notifier) = Create(
            context,
            satMorningUtc,
            (_, _) => new TomorrowHourlyForecastResponse(
                TomorrowCallOutcome.Success,
                [
                    HeavyRainSlot(SundayDepartureUtc),
                    HeavyRainSlot(SundayDepartureUtc.AddHours(1)),
                    SafeSlot(SundayDepartureUtc.AddHours(2)),
                ]));

        await service.EvaluateDueCheckpointsAsync();

        var review = await context.WeatherSafetyReviews.SingleAsync();
        review.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        review.UrgentNoticeSentAtUtc.Should().Be(PriorReceivedUtc);
        (await context.WeatherEvents.SingleAsync()).UpdatedAtUtc.Should().Be(satMorningUtc);
        (await context.Notifications.CountAsync()).Should().Be(0);
        notifier.Calls.Should().BeEmpty();

        // Re-running 15 min later does not poll again for the 24h checkpoint.
        int calls = client.Calls.Count;
        clock.Current = satMorningUtc.AddMinutes(15);
        await service.EvaluateDueCheckpointsAsync();
        client.Calls.Count.Should().Be(calls);
    }

    [Fact]
    public async Task PartialCoverage_MeetingSafeButStopMissingForecastHours_FailsSafeWithoutWithdrawing()
    {
        using var context = await SeedSingleTourAsync(acknowledgeReview: true);
        DateTimeOffset satNightUtc = SundayDepartureUtc.AddHours(-12).AddMinutes(5);
        var (service, _, _, _) = Create(
            context,
            satNightUtc,
            (lat, _) => lat > 16.02
                // Meeting point (16.0544) has safe 08:00 VN slot
                ? new TomorrowHourlyForecastResponse(TomorrowCallOutcome.Success, [SafeSlot(SundayDepartureUtc)])
                // Stop (15.9977, planned 09:00-11:00 VN) only has a slot at 08:00 VN (no overlap with 09:00-11:00!)
                : new TomorrowHourlyForecastResponse(TomorrowCallOutcome.Success, [SafeSlot(SundayDepartureUtc)]));

        int updated = await service.EvaluateDueCheckpointsAsync();

        updated.Should().Be(0);
        var evt = await context.WeatherEvents.SingleAsync();
        evt.IsWithdrawn.Should().BeFalse();
        evt.UpdatedAtUtc.Should().Be(PriorReceivedUtc);
    }

    [Fact]
    public async Task FullPreDepartureTimeline_TriggersExactlyOnceAt24h_12h_6h_AndNeverBetweenCheckpoints()
    {
        using var context = await SeedSingleTourAsync(acknowledgeReview: true);
        var (service, clock, client, notifier) = Create(
            context,
            SundayDepartureUtc.AddHours(-36),
            (_, _) => new TomorrowHourlyForecastResponse(
                TomorrowCallOutcome.Success,
                [
                    HeavyRainSlot(SundayDepartureUtc),
                    HeavyRainSlot(SundayDepartureUtc.AddHours(1)),
                    SafeSlot(SundayDepartureUtc.AddHours(2)),
                ]));

        // T-36h (between 48h PRIOR and 24h checkpoint): 0 calls
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Should().BeEmpty();

        // T-24h01m (1 minute before 24h checkpoint): still 0 calls
        clock.Current = SundayDepartureUtc.AddHours(-24).AddMinutes(-1);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Should().BeEmpty();

        // T-24h (exact 24h checkpoint): triggers once, reopens Acknowledged -> PendingAcknowledgement
        clock.Current = SundayDepartureUtc.AddHours(-24);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(1);
        int callsAfter24h = client.Calls.Count;
        callsAfter24h.Should().BeGreaterThan(0);
        var review = await context.WeatherSafetyReviews.SingleAsync();
        review.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        review.AcknowledgementDeadlineUtc.Should().Be(clock.Current.AddMinutes(30));

        // T-23h45m (15m later) & T-12h01m (after Operator acknowledges): 0 extra calls
        review.Acknowledge(2L, clock.Current.AddMinutes(10));
        await context.SaveChangesAsync();
        clock.Current = SundayDepartureUtc.AddHours(-23).AddMinutes(-45);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        clock.Current = SundayDepartureUtc.AddHours(-12).AddMinutes(-1);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Count.Should().Be(callsAfter24h);

        // T-12h (exact 12h checkpoint): triggers once, reopens again
        clock.Current = SundayDepartureUtc.AddHours(-12);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(1);
        int callsAfter12h = client.Calls.Count;
        callsAfter12h.Should().BeGreaterThan(callsAfter24h);
        review = await context.WeatherSafetyReviews.SingleAsync();
        review.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);

        // Operator acknowledges -> T-6h01m: 0 extra calls
        review.Acknowledge(2L, clock.Current.AddMinutes(10));
        await context.SaveChangesAsync();
        clock.Current = SundayDepartureUtc.AddHours(-6).AddMinutes(-1);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Count.Should().Be(callsAfter12h);

        // T-6h (exact 6h checkpoint): triggers once, reopens third time
        clock.Current = SundayDepartureUtc.AddHours(-6);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(1);
        int callsAfter6h = client.Calls.Count;
        callsAfter6h.Should().BeGreaterThan(callsAfter12h);
        review = await context.WeatherSafetyReviews.SingleAsync();
        review.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);

        // T-5h45m & T-1h (after Operator acknowledges): no more checkpoints -> 0 extra calls
        review.Acknowledge(2L, clock.Current.AddMinutes(10));
        await context.SaveChangesAsync();
        clock.Current = SundayDepartureUtc.AddHours(-5).AddMinutes(-45);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        clock.Current = SundayDepartureUtc.AddHours(-1);
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Count.Should().Be(callsAfter6h);

        // T-0h (departure already started): pre-departure checkpoint job stops completely -> 0 extra calls
        clock.Current = SundayDepartureUtc;
        (await service.EvaluateDueCheckpointsAsync()).Should().Be(0);
        client.Calls.Count.Should().Be(callsAfter6h);

        // Total across the entire 48h -> 0h timeline: 3 reopens (24h, 12h, 6h), 3 Operator pushes, 0 Traveler warnings
        notifier.Calls.Should().HaveCount(3);
        (await context.Notifications.CountAsync(n => n.Type == Notification.WeatherSafetyReviewCreatedType)).Should().Be(3);
        (await context.Notifications.CountAsync(n => n.Type == Notification.WeatherWarningType)).Should().Be(0);
    }

    private static async Task<TestDbContext> SeedSingleTourAsync(
        bool acknowledgeReview,
        string eventType = "CustomInsight",
        DateTimeOffset? createdAtUtc = null,
        string? operatorInstructions = null)
    {
        var context = TestDbContext.Create(Guid.NewGuid().ToString());
        DateTimeOffset created = createdAtUtc ?? PriorReceivedUtc;

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 1L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 2L);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);

        var poi = PointOfInterest.Create(PoiCategory.Create("Landmark", null), "Ba Na Hills",
            15.997700m, 107.988000m, 1, created, "Da Nang", "Desc");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, 100L);
        // Stop planned at 09:00 VN for 120 minutes (09:00-11:00 VN = 02:00-04:00 UTC).
        var stop = TourItineraryItem.Create(1, 1, poi.Id, new TimeOnly(9, 0), 120, "Visit", isRequiredForWeather: true);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.TourId))!.SetValue(stop, tour.Id);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Tour))!.SetValue(stop, tour);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Poi))!.SetValue(stop, poi);
        ((List<TourItineraryItem>)typeof(Tour).GetField("_itineraryItems",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tour)!).Add(stop);

        var schedule = TourSchedule.Create(SundayDepartureUtc, SundayDepartureUtc.AddHours(8), "Da Nang", 20, created.AddDays(-5));
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 10L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        schedule.SetVerifiedMeetingCoordinates(16.054400m, 108.202200m);

        var weatherEvent = WeatherEvent.CreateOfficial(
            "TomorrowIo",
            "alert-1:loc-1:2026-11-29T01:00:00Z",
            eventType,
            WeatherEvent.SeveritySevere,
            SundayDepartureUtc,
            SundayDepartureUtc.AddHours(24),
            "Heavy rain forecast",
            null,
            null,
            null,
            created);

        var review = WeatherSafetyReview.CreateHold(
            schedule.Id,
            "TomorrowIo",
            weatherEvent.ProviderEventId!,
            created,
            created.AddMinutes(30),
            "Initial warning");
        if (acknowledgeReview)
        {
            review.Acknowledge(2L, created.AddMinutes(10), operatorInstructions);
        }

        context.Tours.Add(tour);
        context.PointsOfInterest.Add(poi);
        context.TourItineraryItems.Add(stop);
        context.TourSchedules.Add(schedule);
        context.WeatherEvents.Add(weatherEvent);
        context.WeatherSafetyReviews.Add(review);
        await context.SaveChangesAsync();
        return context;
    }

    private static async Task<TestDbContext> SeedTwoToursSharingEventAsync()
    {
        var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var poi = PointOfInterest.Create(PoiCategory.Create("Landmark", null), "Ba Na Hills",
            15.997700m, 107.988000m, 1, PriorReceivedUtc, "Da Nang", "Desc");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, 100L);
        context.PointsOfInterest.Add(poi);

        var weatherEvent = WeatherEvent.CreateOfficial(
            "TomorrowIo",
            "alert-1:loc-shared:2026-11-29T01:00:00Z",
            "CustomInsight",
            WeatherEvent.SeveritySevere,
            SundayDepartureUtc,
            SundayDepartureUtc.AddHours(24),
            "Heavy rain forecast",
            null,
            null,
            null,
            PriorReceivedUtc);
        context.WeatherEvents.Add(weatherEvent);

        void AddTour(long tourId, long scheduleId, DateTimeOffset startUtc, TimeOnly stopLocalTime)
        {
            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, tourId);
            typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 2L);
            typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);

            var stop = TourItineraryItem.Create(1, 1, poi.Id, stopLocalTime, 60, "Visit", isRequiredForWeather: true);
            typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.TourId))!.SetValue(stop, tourId);
            typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Tour))!.SetValue(stop, tour);
            typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Poi))!.SetValue(stop, poi);
            ((List<TourItineraryItem>)typeof(Tour).GetField("_itineraryItems",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tour)!).Add(stop);

            var schedule = TourSchedule.Create(startUtc, startUtc.AddHours(3), "Da Nang", 20, PriorReceivedUtc.AddDays(-5));
            typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, scheduleId);
            typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tourId);
            typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
            // Same MonitoringCell as POI so both tours deduplicate to 1 API call.
            schedule.SetVerifiedMeetingCoordinates(15.997700m, 107.988000m);

            var review = WeatherSafetyReview.CreateHold(
                scheduleId,
                "TomorrowIo",
                weatherEvent.ProviderEventId!,
                PriorReceivedUtc,
                PriorReceivedUtc.AddMinutes(30),
                "Initial warning");
            review.Acknowledge(2L, PriorReceivedUtc.AddMinutes(10));

            context.Tours.Add(tour);
            context.TourItineraryItems.Add(stop);
            context.TourSchedules.Add(schedule);
            context.WeatherSafetyReviews.Add(review);
        }

        // Tour 1: 08:00 VN start, stop at 09:00-10:00 VN (safe)
        AddTour(1L, 10L, SundayDepartureUtc, new TimeOnly(9, 0));
        // Tour 2: 10:00 VN start, stop at 11:00-12:00 VN (heavy rain!)
        AddTour(2L, 20L, SundayDepartureUtc.AddHours(2), new TimeOnly(11, 0));

        await context.SaveChangesAsync();
        return context;
    }
}
using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.Emergency;
using TripMate.Application.Features.Weather.Matching;
using TripMate.Application.Features.Weather.Processing;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Weather;

namespace TripMate.Application.UnitTests.Features.Weather.Processing;

public sealed class WeatherAlertInboxProcessorTests
{
    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private readonly TestTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
    private readonly TomorrowAlertNormalizer _normalizer = new();

    private sealed class InMemoryInboxStore : IWeatherAlertInboxStore
    {
        public bool ProcessedCalled { get; private set; }
        public bool FailedCalled { get; private set; }
        public string? LastError { get; private set; }

        public Task<IReadOnlyList<WeatherAlertInboxItem>> LeasePendingItemsAsync(int batchSize, TimeSpan leaseDuration, string leasedBy, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WeatherAlertInboxItem>>([]);

        public Task<bool> EnqueueIfNotExistsAsync(string provider, string providerNotificationId, string payload, string? signature, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task MarkProcessedAsync(long inboxId, CancellationToken cancellationToken)
        {
            ProcessedCalled = true;
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(long inboxId, string errorMessage, CancellationToken cancellationToken)
        {
            FailedCalled = true;
            LastError = errorMessage;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProcessInboxItem_WhenNewOfficialEvent_CreatesWeatherEventInDatabase()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var inboxStore = new InMemoryInboxStore();
        var processor = new WeatherAlertInboxProcessor(
            context,
            [_normalizer],
            inboxStore,
            _timeProvider,
            NullLogger<WeatherAlertInboxProcessor>.Instance);

        string payload = """
        {
            "id": "evt-storm-101",
            "start": "2026-11-03T08:00:00Z",
            "end": "2026-11-03T18:00:00Z",
            "updateTime": "2026-11-02T09:00:00Z",
            "headline": "Cảnh báo từ Trung tâm Dự báo Khí tượng Thủy văn Quốc gia NCHMF",
            "description": "Rủi ro thiên tai cấp 3",
            "severity": "Severe"
        }
        """;

        var inboxItem = WeatherAlertInboxItem.CreatePending("TomorrowIo", payload, "notif-101", null, _timeProvider.GetUtcNow());
        context.WeatherAlertInboxItems.Add(inboxItem);
        await context.SaveChangesAsync();

        await processor.ProcessInboxItemAsync(inboxItem, CancellationToken.None);

        inboxStore.ProcessedCalled.Should().BeTrue();
        var savedEvent = await context.WeatherEvents.FirstOrDefaultAsync(e => e.ProviderEventId == "evt-storm-101");
        savedEvent.Should().NotBeNull();
        savedEvent!.OfficialVietnamRiskLevel.Should().Be(3);
        savedEvent.Description.Should().NotBeNullOrWhiteSpace();
        savedEvent.Description.Should().Contain("3");
        savedEvent.OriginalAuthority.Should().Contain("Trung tâm Dự báo Khí tượng Thủy văn Quốc gia");
        savedEvent.IsWithdrawn.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessInboxItem_WhenNewerRevisionArrives_UpdatesExistingWeatherEvent()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var inboxStore = new InMemoryInboxStore();
        var processor = new WeatherAlertInboxProcessor(
            context,
            [_normalizer],
            inboxStore,
            _timeProvider,
            NullLogger<WeatherAlertInboxProcessor>.Instance);

        var existing = WeatherEvent.CreateOfficial(
            provider: "TomorrowIo",
            providerEventId: "evt-storm-102",
            eventType: "SevereWeather",
            severity: "Moderate",
            validFromUtc: _timeProvider.GetUtcNow().AddHours(1),
            validToUtc: _timeProvider.GetUtcNow().AddHours(10),
            description: "Initial warning",
            originalAuthority: "NCHMF",
            originalSourceReference: null,
            officialVietnamRiskLevel: 2,
            verifiedDirective: null,
            geometryGeoJson: null,
            rawPayloadHash: "hash1",
            ingestedAtUtc: _timeProvider.GetUtcNow().AddHours(-2));
        context.WeatherEvents.Add(existing);
        await context.SaveChangesAsync();

        string upgradedPayload = """
        {
            "id": "evt-storm-102",
            "start": "2026-11-03T08:00:00Z",
            "end": "2026-11-03T18:00:00Z",
            "updateTime": "2026-11-02T11:00:00Z",
            "headline": "Cảnh báo từ NCHMF",
            "description": "Rủi ro thiên tai cấp 4. Yêu cầu cấm biển.",
            "severity": "Extreme"
        }
        """;

        var inboxItem = WeatherAlertInboxItem.CreatePending("TomorrowIo", upgradedPayload, "notif-102", null, _timeProvider.GetUtcNow());
        await processor.ProcessInboxItemAsync(inboxItem, CancellationToken.None);

        inboxStore.ProcessedCalled.Should().BeTrue();
        var updated = await context.WeatherEvents.FirstOrDefaultAsync(e => e.ProviderEventId == "evt-storm-102");
        updated.Should().NotBeNull();
        updated!.OfficialVietnamRiskLevel.Should().Be(4);
        updated.VerifiedDirective.Should().Be("TravelProhibition");
        updated.Severity.Should().Be("Extreme");
        updated.Description.Should().Contain("4");
    }

    [Fact]
    public async Task ProcessInboxItem_WhenOlderReplayArrives_IgnoresStaleReplay()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var inboxStore = new InMemoryInboxStore();
        var processor = new WeatherAlertInboxProcessor(
            context,
            [_normalizer],
            inboxStore,
            _timeProvider,
            NullLogger<WeatherAlertInboxProcessor>.Instance);

        var existing = WeatherEvent.CreateOfficial(
            provider: "TomorrowIo",
            providerEventId: "evt-storm-103",
            eventType: "SevereWeather",
            severity: "Extreme",
            validFromUtc: _timeProvider.GetUtcNow().AddHours(1),
            validToUtc: _timeProvider.GetUtcNow().AddHours(10),
            description: "Newer alert",
            originalAuthority: "NCHMF",
            originalSourceReference: null,
            officialVietnamRiskLevel: 4,
            verifiedDirective: "TravelProhibition",
            geometryGeoJson: null,
            rawPayloadHash: "hash-new",
            ingestedAtUtc: _timeProvider.GetUtcNow().AddHours(1)); // Updated at T+1
        context.WeatherEvents.Add(existing);
        await context.SaveChangesAsync();

        // Older replay with T-1 updateTime
        string olderPayload = """
        {
            "id": "evt-storm-103",
            "start": "2026-11-03T08:00:00Z",
            "end": "2026-11-03T18:00:00Z",
            "updateTime": "2026-11-02T08:00:00Z",
            "headline": "Cảnh báo cũ từ NCHMF",
            "description": "Rủi ro thiên tai cấp 2",
            "severity": "Moderate"
        }
        """;

        var inboxItem = WeatherAlertInboxItem.CreatePending("TomorrowIo", olderPayload, "notif-103", null, _timeProvider.GetUtcNow());
        await processor.ProcessInboxItemAsync(inboxItem, CancellationToken.None);

        inboxStore.ProcessedCalled.Should().BeTrue();
        var current = await context.WeatherEvents.FirstOrDefaultAsync(e => e.ProviderEventId == "evt-storm-103");
        current.Should().NotBeNull();
        current!.OfficialVietnamRiskLevel.Should().Be(4, "older replay must not downgrade newer evidence");
        current.Severity.Should().Be("Extreme");
    }

    [Fact]
    public async Task ProcessInboxItem_WhenEventIsWithdrawn_MarksExistingWeatherEventWithdrawn()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var inboxStore = new InMemoryInboxStore();
        var processor = new WeatherAlertInboxProcessor(
            context,
            [_normalizer],
            inboxStore,
            _timeProvider,
            NullLogger<WeatherAlertInboxProcessor>.Instance);

        var existing = WeatherEvent.CreateOfficial(
            provider: "TomorrowIo",
            providerEventId: "evt-storm-104",
            eventType: "SevereWeather",
            severity: "Severe",
            validFromUtc: _timeProvider.GetUtcNow().AddHours(1),
            validToUtc: _timeProvider.GetUtcNow().AddHours(10),
            description: "Active alert",
            originalAuthority: "NCHMF",
            originalSourceReference: null,
            officialVietnamRiskLevel: 3,
            verifiedDirective: null,
            geometryGeoJson: null,
            rawPayloadHash: "hash",
            ingestedAtUtc: _timeProvider.GetUtcNow());
        context.WeatherEvents.Add(existing);
        await context.SaveChangesAsync();

        string withdrawalPayload = """
        {
            "id": "evt-storm-104",
            "status": "Withdrawn",
            "action": "CANCEL",
            "start": "2026-11-03T08:00:00Z",
            "end": "2026-11-03T18:00:00Z",
            "updateTime": "2026-11-02T12:00:00Z",
            "headline": "Bão đã tan"
        }
        """;

        var inboxItem = WeatherAlertInboxItem.CreatePending("TomorrowIo", withdrawalPayload, "notif-104", null, _timeProvider.GetUtcNow());
        await processor.ProcessInboxItemAsync(inboxItem, CancellationToken.None);

        inboxStore.ProcessedCalled.Should().BeTrue();
        var updated = await context.WeatherEvents.FirstOrDefaultAsync(e => e.ProviderEventId == "evt-storm-104");
        updated.Should().NotBeNull();
        updated!.IsWithdrawn.Should().BeTrue();
    }

    private sealed class FixedDepartureFinder(WeatherDepartureCandidate candidate) : IWeatherAffectedDepartureFinder
    {
        public Task<IReadOnlyList<WeatherDepartureCandidate>> FindAffectedDeparturesAsync(
            NormalizedWeatherEvent weatherEvent,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WeatherDepartureCandidate>>([candidate]);
    }

    private static string StormPayload(string updateTime, string description) => $$"""
        {
            "id": "evt-storm-103",
            "start": "2026-11-03T08:00:00Z",
            "end": "2026-11-03T18:00:00Z",
            "updateTime": "{{updateTime}}",
            "headline": "Cảnh báo từ NCHMF",
            "description": "{{description}}",
            "severity": "Extreme",
            "geometry": {
                "type": "Polygon",
                "coordinates": [[[107.0, 15.0], [109.0, 15.0], [109.0, 17.0], [107.0, 17.0], [107.0, 15.0]]]
            }
        }
        """;

    [Fact]
    public async Task ProcessInboxItem_WhenRevisionFollowsAcknowledgedReview_KeepsOneReviewAndOneEvaluation()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var inboxStore = new InMemoryInboxStore();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset start = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 1L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 2L);
        var schedule = TourSchedule.Create(start, start.AddHours(8), "Da Nang", 20, now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 5L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, 1L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        context.Tours.Add(tour);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();
        var finder = new FixedDepartureFinder(new WeatherDepartureCandidate(schedule,
            new WeatherDepartureImpact(start, start.AddHours(8), new WeatherPoint(16m, 108m), [], true)));
        var processor = new WeatherAlertInboxProcessor(
            context,
            [_normalizer],
            inboxStore,
            _timeProvider,
            NullLogger<WeatherAlertInboxProcessor>.Instance,
            departureFinder: finder);

        await processor.ProcessInboxItemAsync(
            WeatherAlertInboxItem.CreatePending("TomorrowIo",
                StormPayload("2026-11-02T09:00:00Z", "Rủi ro thiên tai cấp 3."), "notif-103-a", null, now),
            CancellationToken.None);
        WeatherSafetyReview review = await context.WeatherSafetyReviews.SingleAsync();
        review.Acknowledge(2L, now);
        await context.SaveChangesAsync();

        await processor.ProcessInboxItemAsync(
            WeatherAlertInboxItem.CreatePending("TomorrowIo",
                StormPayload("2026-11-02T10:30:00Z", "Rủi ro thiên tai cấp 4. Yêu cầu cấm biển."), "notif-103-b", null, now),
            CancellationToken.None);

        (await context.WeatherSafetyReviews.CountAsync()).Should().Be(1, "the unique index allows one review per departure and event");
        WeatherEventEvaluation evaluation = await context.WeatherEventEvaluations.SingleAsync();
        evaluation.ReasonCode.Should().Be("weather.verified_directive_operator_review");
    }

    [Fact]
    public async Task ProcessInboxItem_WhenPayloadMalformed_MarksInboxItemFailed()
    {
        using var context = TestDbContext.Create(Guid.NewGuid().ToString());
        var inboxStore = new InMemoryInboxStore();
        var processor = new WeatherAlertInboxProcessor(
            context,
            [_normalizer],
            inboxStore,
            _timeProvider,
            NullLogger<WeatherAlertInboxProcessor>.Instance);

        var inboxItem = WeatherAlertInboxItem.CreatePending("TomorrowIo", "this is not json", "notif-105", null, _timeProvider.GetUtcNow());
        await processor.ProcessInboxItemAsync(inboxItem, CancellationToken.None);

        inboxStore.FailedCalled.Should().BeTrue();
        inboxStore.LastError.Should().NotBeNullOrEmpty();
    }
}

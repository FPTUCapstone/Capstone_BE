using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.List;
using TripMate.Application.Features.TripAlerts.Scanning;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.TripAlerts;

public sealed class PairwiseScenariosTests
{
    private const long TravelerId = 101;
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    public sealed record PairwiseCaseDefinition(
        string Id,
        double RainMmPerHour,
        bool Thunderstorm,
        bool IsProviderFailure,
        IndoorOutdoorType PoiClassification,
        bool Reviewed,
        int EtaMinutes,
        bool IsOverdue,
        bool IsStale,
        string TripState,
        string ExpectedOutput);

    public static readonly PairwiseCaseDefinition[] Cases =
    [
        new("PW-01", 2.49, false, false, IndoorOutdoorType.Outdoor, true, 59, false, false, "ActivePending", "No rain output"),
        new("PW-02", 2.50, false, false, IndoorOutdoorType.Indoor, true, 61, false, false, "ActivePending", "No rain output"),
        new("PW-03", 9.99, false, false, IndoorOutdoorType.Outdoor, false, 179, false, false, "ActivePending", "No rain output"),
        new("PW-04", 10.00, false, false, IndoorOutdoorType.Outdoor, false, 181, false, false, "ItemResolved", "No new output; resolve existing"),
        new("PW-05", 49.99, false, false, IndoorOutdoorType.Indoor, true, -5, true, false, "ItemResolved", "No new output; resolve existing"),
        new("PW-06", 50.00, false, false, IndoorOutdoorType.Outdoor, true, -15, true, true, "ItemResolved", "No new output; resolve existing"),
        new("PW-07", 10.00, true, false, IndoorOutdoorType.Outdoor, true, -5, true, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-08", 0, false, true, IndoorOutdoorType.Indoor, true, 181, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-09", 0, false, true, IndoorOutdoorType.Outdoor, false, -15, true, true, "ActivePending", "No new output; retain existing"),
        new("PW-10", 10.00, true, false, IndoorOutdoorType.Outdoor, false, 59, false, false, "ItemResolved", "No new output; resolve existing"),
        new("PW-11", 50.00, false, false, IndoorOutdoorType.Outdoor, false, 61, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-12", 49.99, false, false, IndoorOutdoorType.Outdoor, true, 179, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-13", 10.00, false, false, IndoorOutdoorType.Indoor, true, 179, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-14", 9.99, false, false, IndoorOutdoorType.Indoor, true, 59, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-15", 2.50, false, false, IndoorOutdoorType.Outdoor, true, 181, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-16", 2.49, false, false, IndoorOutdoorType.Outdoor, false, -5, true, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-17", 2.49, false, false, IndoorOutdoorType.Indoor, true, -15, true, true, "SessionEnded", "No new output; resolve existing"),
        new("PW-18", 2.50, false, false, IndoorOutdoorType.Outdoor, false, 179, false, false, "ItemResolved", "No new output; resolve existing"),
        new("PW-19", 9.99, false, false, IndoorOutdoorType.Outdoor, true, 61, false, false, "ItemResolved", "No new output; resolve existing"),
        new("PW-20", 10.00, false, false, IndoorOutdoorType.Outdoor, true, -5, true, false, "ActivePending", "Warning + CurrentAtDestination + one push"),
        new("PW-21", 49.99, false, false, IndoorOutdoorType.Outdoor, false, 181, false, false, "ActivePending", "No new output"),
        new("PW-22", 50.00, false, false, IndoorOutdoorType.Indoor, true, 181, false, false, "ActivePending", "No new output"),
        new("PW-23", 10.00, true, false, IndoorOutdoorType.Indoor, true, 181, false, false, "ActivePending", "No new output"),
        new("PW-24", 0, false, true, IndoorOutdoorType.Outdoor, true, 179, false, false, "ItemResolved", "No new output; resolve existing"),
        new("PW-25", 2.49, false, false, IndoorOutdoorType.Outdoor, true, 181, false, false, "ItemResolved", "No new output; resolve existing"),
        new("PW-26", 0, false, true, IndoorOutdoorType.Outdoor, true, -5, true, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-27", 10.00, true, false, IndoorOutdoorType.Outdoor, true, 179, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-28", 50.00, false, false, IndoorOutdoorType.Outdoor, true, 179, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-29", 49.99, false, false, IndoorOutdoorType.Outdoor, true, 59, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-30", 10.00, false, false, IndoorOutdoorType.Outdoor, true, 59, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-31", 9.99, false, false, IndoorOutdoorType.Outdoor, true, 181, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-32", 2.50, false, false, IndoorOutdoorType.Outdoor, true, -5, true, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-33", 2.50, false, false, IndoorOutdoorType.Outdoor, true, 59, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-34", 9.99, false, false, IndoorOutdoorType.Outdoor, true, -5, true, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-35", 10.00, false, false, IndoorOutdoorType.Outdoor, true, -15, true, true, "SessionEnded", "No new output; resolve existing"),
        new("PW-36", 49.99, false, false, IndoorOutdoorType.Outdoor, true, -15, true, true, "SessionEnded", "No new output; resolve existing"),
        new("PW-37", 50.00, false, false, IndoorOutdoorType.Outdoor, true, -5, true, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-38", 10.00, true, false, IndoorOutdoorType.Outdoor, true, -15, true, true, "SessionEnded", "No new output; resolve existing"),
        new("PW-39", 0, false, true, IndoorOutdoorType.Outdoor, true, 59, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-40", 2.49, false, false, IndoorOutdoorType.Outdoor, true, 179, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-41", 2.49, false, false, IndoorOutdoorType.Outdoor, true, 61, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-42", 0, false, true, IndoorOutdoorType.Outdoor, true, 61, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-43", 10.00, true, false, IndoorOutdoorType.Outdoor, true, 61, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-44", 50.00, false, false, IndoorOutdoorType.Outdoor, true, 59, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-45", 49.99, false, false, IndoorOutdoorType.Outdoor, true, 61, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-46", 10.00, false, false, IndoorOutdoorType.Outdoor, true, 61, false, false, "SessionEnded", "No new output; resolve existing"),
        new("PW-47", 9.99, false, false, IndoorOutdoorType.Outdoor, true, -15, true, true, "SessionEnded", "No new output; resolve existing"),
        new("PW-48", 2.50, false, false, IndoorOutdoorType.Outdoor, true, -15, true, true, "SessionEnded", "No new output; resolve existing"),
    ];

    public static IEnumerable<object[]> GetPairwiseTestCases() =>
        Cases.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(GetPairwiseTestCases))]
    public async Task ExecutePairwiseCaseAsync(string caseId)
    {
        var c = Cases.Single(item => item.Id == caseId);
        await using var db = TestDbContext.Create();

        var plannedArrival = Now.AddMinutes(c.EtaMinutes);
        var seed = await SeedAsync(db, plannedArrival, c.PoiClassification, c.Reviewed);

        // Weather basis
        var basis = c.IsOverdue ? TripWeatherBasis.CurrentAtDestination : TripWeatherBasis.ForecastAtPlannedTime;
        var weatherSnapshot = c.IsStale
            ? null // Stale or missing realtime returns null from provider
            : new TripWeatherSnapshot(
                c.IsOverdue ? Now : plannedArrival,
                c.RainMmPerHour,
                c.Thunderstorm,
                null,
                [],
                basis);

        var weatherProvider = new MockWeatherProvider
        {
            Snapshot = weatherSnapshot,
            ShouldTimeout = c.IsProviderFailure,
        };

        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var options = Options.Create(new TripAlertOptions());
        var scanner = new TripAlertScanner(db, clock, options, weatherProvider);

        // Pre-condition state setup
        if (c.TripState == "ItemResolved")
        {
            // Seed an active incident first to verify scanner resolves it
            await SeedOpenIncidentAsync(db, seed.SessionId, seed.ItemId);

            // Mark item as Reached
            var session = await db.TripSessions.Include(s => s.Items).SingleAsync(s => s.Id == seed.SessionId);
            session.ReachItem(seed.ItemId, Now.AddMinutes(-1));
            await db.SaveChangesAsync();

            // Execute scanner
            await scanner.ScanAsync(CancellationToken.None);

            // Assertions
            var incidents = await db.Incidents.Where(i => i.SessionId == seed.SessionId).ToListAsync();
            incidents.Should().ContainSingle();
            incidents[0].ResolvedAtUtc.Should().NotBeNull("scanner must resolve existing incident when item is reached");
            var unresolvedCount = await db.Incidents.CountAsync(i => i.SessionId == seed.SessionId && i.ResolvedAtUtc == null);
            unresolvedCount.Should().Be(0, "no new open incidents should exist");
        }
        else if (c.TripState == "SessionEnded")
        {
            // Seed an active incident first to verify scanner resolves it
            await SeedOpenIncidentAsync(db, seed.SessionId, seed.ItemId);

            // End session
            var session = await db.TripSessions.SingleAsync(s => s.Id == seed.SessionId);
            session.Finish(Now.AddMinutes(-1));
            await db.SaveChangesAsync();

            // Execute scanner
            await scanner.ScanAsync(CancellationToken.None);

            // Assertions
            var incidents = await db.Incidents.Where(i => i.SessionId == seed.SessionId).ToListAsync();
            incidents.Should().ContainSingle();
            incidents[0].ResolvedAtUtc.Should().NotBeNull("scanner must resolve existing incident when session has ended");
            var unresolvedCount = await db.Incidents.CountAsync(i => i.SessionId == seed.SessionId && i.ResolvedAtUtc == null);
            unresolvedCount.Should().Be(0, "no new open incidents should exist for ended session");
        }
        else // ActivePending
        {
            if (c.Id == "PW-09")
            {
                // PW-09: ProviderFailure, Unreviewed, OverdueStale, ActivePending
                // Expected: No new output; retain existing
                await SeedOpenIncidentAsync(db, seed.SessionId, seed.ItemId);

                // Run scanner with failing provider
                await scanner.ScanAsync(CancellationToken.None);

                var incidents = await db.Incidents.Where(i => i.SessionId == seed.SessionId).ToListAsync();
                incidents.Should().ContainSingle();
                incidents[0].ResolvedAtUtc.Should().BeNull("existing alert must be retained on provider failure");
            }
            else if (c.Id == "PW-20")
            {
                // PW-20: Rain 10.00, ReviewedOutdoor, OverdueFresh, ActivePending
                // Expected: Warning + CurrentAtDestination + one push
                await scanner.ScanAsync(CancellationToken.None);

                var incidents = await db.Incidents.Where(i => i.SessionId == seed.SessionId).ToListAsync();
                incidents.Should().ContainSingle();
                var alert = incidents[0];
                alert.IncidentType.Should().Be(Incident.SevereWeatherType);
                alert.WeatherBasis.Should().Be(TripWeatherBasis.CurrentAtDestination.ToString());
                alert.RainIntensityMmPerHour.Should().Be(10.0);
                alert.ResolvedAtUtc.Should().BeNull();

                var notifications = await db.Notifications.Where(n => n.RelatedEntityId == alert.Id).ToListAsync();
                notifications.Should().ContainSingle();
                notifications[0].Status.Should().Be(NotificationStatus.Pending);

                var deliveryWork = await db.AlertDeliveryWork.Where(w => w.NotificationId == notifications[0].Id).ToListAsync();
                deliveryWork.Should().ContainSingle();
                deliveryWork[0].AttemptCount.Should().Be(0);

                var listResult = await new ListTripAlertsQueryHandler(db)
                    .Handle(new ListTripAlertsQuery(seed.SessionId, TravelerId), CancellationToken.None);
                listResult.IsSuccess.Should().BeTrue();
                var itemAlert = listResult.Value.Single(a => a.IncidentId == alert.Id);
                itemAlert.CanSkipAffectedStop.Should().BeTrue();
                itemAlert.WeatherCondition.Should().Be("HeavyRain");
            }
            else
            {
                // PW-01, PW-02, PW-03, PW-21, PW-22, PW-23: Expected no rain output
                await scanner.ScanAsync(CancellationToken.None);

                var incidents = await db.Incidents.Where(i => i.SessionId == seed.SessionId).ToListAsync();
                incidents.Should().BeEmpty($"case {c.Id} expects no rain alert created");

                var notifications = await db.Notifications.Where(n => n.UserId == TravelerId).ToListAsync();
                notifications.Should().BeEmpty();

                var deliveryWork = await db.AlertDeliveryWork.ToListAsync();
                deliveryWork.Should().BeEmpty();
            }
        }
    }

    private static async Task SeedOpenIncidentAsync(TestDbContext db, long sessionId, long itemId)
    {
        var session = await db.TripSessions.SingleAsync(s => s.Id == sessionId);
        var incident = Incident.CreateTripAlert(
            session,
            Incident.SevereWeatherType,
            $"tomorrow:rain-heavy:{itemId}:{Now:O}",
            Now.AddMinutes(-10),
            itemId,
            null,
            "Heavy rain warning",
            TripWeatherBasis.ForecastAtPlannedTime.ToString(),
            Now,
            15.0);
        db.Incidents.Add(incident);
        await db.SaveChangesAsync();

        var notification = new Notification
        {
            UserId = session.TravelerUserId,
            Channel = NotificationChannel.Push,
            Type = Notification.TripAlertType,
            Title = "Trip alert",
            Body = "Severe weather warning",
            RelatedEntityType = Notification.IncidentRelatedEntityType,
            RelatedEntityId = incident.Id,
            Status = NotificationStatus.Sent,
            CreatedAtUtc = Now.AddMinutes(-10).UtcDateTime,
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        db.AlertDeliveryWork.Add(AlertDeliveryWork.Create(notification, Now.AddMinutes(-10)));
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();
    }

    private static async Task<SeedResult> SeedAsync(
        TestDbContext db,
        DateTimeOffset plannedArrivalUtc,
        IndoorOutdoorType poiClassification,
        bool reviewed)
    {
        var user = new User
        {
            Id = TravelerId,
            Email = $"traveler{TravelerId}@example.com",
            FullName = "Test Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now.AddDays(-1),
            UpdatedAtUtc = Now.AddDays(-1),
        };
        var category = PoiCategory.Create("Category", null);
        db.AddRange(user, category);
        await db.SaveChangesAsync();

        var poi = PointOfInterest.Create(category, "Test POI", 16.05m, 108.20m, TravelerId, Now.AddDays(-1));
        if (reviewed)
        {
            poi.ReviewIndoorOutdoor(poiClassification, Now.AddDays(-1));
        }
        else
        {
            // Unreviewed defaults to Outdoor, but reviewed_at is null
            db.Entry(poi).Property(p => p.IndoorOutdoor).CurrentValue = poiClassification;
            db.Entry(poi).Property(p => p.IndoorOutdoorReviewedAtUtc).CurrentValue = null;
        }

        db.Add(poi);
        await db.SaveChangesAsync();

        var itinerary = Itinerary.CreateManual(TravelerId, "Itinerary", Itinerary.ActiveStatus, Now.AddDays(-1));
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1, poi.Id, plannedArrivalUtc, plannedArrivalUtc.AddMinutes(30), true, 0, "Planned"));
        db.Add(itinerary);
        await db.SaveChangesAsync();

        var item = itinerary.Items.Single();
        var session = TripSession.Start(
            itinerary.Id, itinerary.Id, TravelerId, Guid.NewGuid(), Now.AddHours(-1), Now.AddHours(4),
            [TripSessionItem.Snapshot(
                item.Id, 1, poi.Id, poi.Name, poi.Latitude, poi.Longitude,
                item.PlannedArrivalUtc!.Value, item.PlannedDepartureUtc!.Value, true)]);
        db.Add(session);
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();

        return new SeedResult(session.Id, item.Id, poi.Id);
    }

    private sealed class MockWeatherProvider : ITripWeatherProvider
    {
        public TripWeatherSnapshot? Snapshot { get; set; }
        public bool ShouldTimeout { get; set; }
        public int CallCount { get; private set; }

        public Task<TripWeatherSnapshot?> GetAsync(
            WeatherForecastRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (ShouldTimeout)
            {
                throw new TaskCanceledException("Tomorrow.io provider timed out or connection failed.");
            }

            return Task.FromResult(Snapshot);
        }
    }

    private sealed record SeedResult(long SessionId, long ItemId, long PoiId);
}
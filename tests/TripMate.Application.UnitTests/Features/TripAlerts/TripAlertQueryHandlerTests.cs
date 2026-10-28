using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TripAlerts.Acknowledge;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.List;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripAlerts;

public sealed class TripAlertQueryHandlerTests
{
    private const long TravelerId = 44;
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task List_ReturnsOnlyOwnedTripAlertsNewestFirstWithServerPresentationContract()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);

        var result = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(seed.SessionId, TravelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Select(alert => alert.IncidentId)
            .Should().Equal(seed.NewestIncidentId, seed.OlderIncidentId);
        var delay = result.Value.Single(alert => alert.Type == Incident.ScheduleDelayType);
        delay.Severity.Should().Be(TripAlertResponse.WarningSeverity);
        delay.MessageCode.Should().Be(TripAlertResponse.ScheduleDelayMessageCode);
        delay.AffectedItem!.Name.Should().Be("Dragon Bridge");
        delay.IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task Acknowledge_IsIdempotentAndReadIsTerminalAfterIncidentResolution()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);
        var clock = new FakeDateTimeProvider { UtcNow = Now.AddMinutes(5) };
        var handler = new AcknowledgeTripAlertCommandHandler(db, clock);

        var first = await handler.Handle(
            new AcknowledgeTripAlertCommand(seed.SessionId, seed.OlderIncidentId, TravelerId),
            CancellationToken.None);
        var second = await handler.Handle(
            new AcknowledgeTripAlertCommand(seed.SessionId, seed.OlderIncidentId, TravelerId),
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        var stored = await db.Notifications.SingleAsync(notification =>
            notification.RelatedEntityId == seed.OlderIncidentId
            && notification.Type == Notification.TripAlertType);
        stored.Status.Should().Be(NotificationStatus.Read);
        stored.ReadAtUtc.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public async Task List_ForAnotherTravelerIsForbiddenWithoutDisclosingAlerts()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);

        var result = await new ListTripAlertsQueryHandler(db)
            .Handle(new ListTripAlertsQuery(seed.SessionId, 999), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TripAlertErrorCodes.AccessDenied);
    }

    private static TestDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static async Task<Seed> SeedAsync(TestDbContext db)
    {
        var traveler = new User
        {
            Id = TravelerId,
            Email = "uc14@example.com",
            FullName = "UC14 Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var category = PoiCategory.Create("UC14", null);
        db.Users.Add(traveler);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();
        var poi = PointOfInterest.Create(category, "Dragon Bridge", 16m, 108m, TravelerId, Now);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(TravelerId, "UC14", Itinerary.ActiveStatus, Now);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1, poi.Id, Now.AddHours(1), Now.AddHours(2), true, 0m, "Planned"));
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        var itineraryItem = itinerary.Items.Single();
        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            TravelerId,
            Guid.NewGuid(),
            Now,
            Now.AddHours(6),
            [TripSessionItem.Snapshot(
                itineraryItem.Id, 1, poi.Id, poi.Name, poi.Latitude, poi.Longitude,
                Now.AddHours(1), Now.AddHours(2), true)]);
        db.TripSessions.Add(session);
        await db.SaveChangesAsync();

        var older = Incident.CreateTripAlert(
            session, Incident.ScheduleDelayType, $"delay:{itineraryItem.Id}", Now,
            itineraryItem.Id);
        var newest = Incident.CreateTripAlert(
            session, Incident.PoiClosureType, $"closure:{itineraryItem.Id}", Now.AddMinutes(1),
            itineraryItem.Id);
        db.Incidents.AddRange(older, newest);
        await db.SaveChangesAsync();
        older.Resolve(Now.AddMinutes(2));
        db.Notifications.AddRange(
            TripAlertNotification(traveler, older.Id, Now),
            TripAlertNotification(traveler, newest.Id, Now.AddMinutes(1)),
            new Notification
            {
                User = traveler,
                Channel = NotificationChannel.Email,
                Type = "Unrelated",
                RelatedEntityType = Notification.IncidentRelatedEntityType,
                RelatedEntityId = older.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = Now,
            });
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();
        return new Seed(session.Id, older.Id, newest.Id);
    }

    private static Notification TripAlertNotification(
        User user,
        long incidentId,
        DateTimeOffset createdAtUtc) => new()
        {
            User = user,
            Channel = NotificationChannel.Push,
            Type = Notification.TripAlertType,
            RelatedEntityType = Notification.IncidentRelatedEntityType,
            RelatedEntityId = incidentId,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = createdAtUtc,
        };

    private sealed record Seed(long SessionId, long OlderIncidentId, long NewestIncidentId);
}
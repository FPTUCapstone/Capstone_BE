using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.ReportDeviation;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripAlerts;

public sealed class ReportDeviationCommandHandlerTests
{
    private const long TravelerId = 81;
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreatedEvent_ReplaysSameIncidentEvenAfterSessionCompletes()
    {
        await using var db = TestDbContext.Create();
        var seed = await SeedAsync(db);
        var handler = Handler(db);
        var eventId = Guid.NewGuid();
        var command = new ReportDeviationCommand(
            seed.SessionId, TravelerId, eventId, Now, seed.FirstItemId);

        var created = await handler.Handle(command, CancellationToken.None);
        var session = await db.TripSessions.Include(item => item.Items)
            .SingleAsync(item => item.Id == seed.SessionId);
        session.Finish(Now.AddMinutes(1));
        await db.SaveChangesAsync();
        var replayed = await handler.Handle(command, CancellationToken.None);

        created.IsSuccess.Should().BeTrue();
        replayed.IsSuccess.Should().BeTrue();
        replayed.Value.IncidentId.Should().Be(created.Value.IncidentId);
        (await db.Incidents.CountAsync()).Should().Be(1);
        (await db.Notifications.CountAsync(item => item.Type == Notification.TripAlertType)).Should().Be(1);
        (await db.AlertDeliveryWork.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DifferentTargetWithinCooldown_Returns429MetadataAndCreatesNothingElse()
    {
        await using var db = TestDbContext.Create();
        var seed = await SeedAsync(db);
        var clock = new FakeDateTimeProvider { UtcNow = Now };
        var handler = Handler(db, clock);
        await handler.Handle(new ReportDeviationCommand(
            seed.SessionId, TravelerId, Guid.NewGuid(), Now, seed.FirstItemId), CancellationToken.None);

        clock.UtcNow = Now.AddMinutes(1);

        var blocked = await handler.Handle(new ReportDeviationCommand(
            seed.SessionId, TravelerId, Guid.NewGuid(), Now.AddMinutes(1), seed.SecondItemId),
            CancellationToken.None);

        blocked.IsFailure.Should().BeTrue();
        blocked.ErrorCode.Should().Be(TripAlertErrorCodes.DeviationCooldown);
        blocked.ErrorMetadata["retryAfterSeconds"].Should().Be(240);
        (await db.Incidents.CountAsync()).Should().Be(1);
    }

    private static ReportDeviationCommandHandler Handler(
        TestDbContext db,
        FakeDateTimeProvider? clock = null) => new(
        db,
        clock ?? new FakeDateTimeProvider { UtcNow = Now },
        Options.Create(new TripAlertOptions()));

    private static async Task<Seed> SeedAsync(TestDbContext db)
    {
        var user = new User
        {
            Id = TravelerId,
            Email = "deviation@example.com",
            FullName = "Deviation",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var category = PoiCategory.Create("Deviation", null);
        db.AddRange(user, category);
        await db.SaveChangesAsync();
        var poi = PointOfInterest.Create(category, "Target", 16m, 108m, TravelerId, Now);
        db.Add(poi);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(TravelerId, "Deviation", Itinerary.ActiveStatus, Now);
        itinerary.AddItem(ItineraryItem.CreateVisit(1, poi.Id, Now.AddHours(1), Now.AddHours(2), true, 0, "Planned"));
        itinerary.AddItem(ItineraryItem.CreateVisit(2, poi.Id, Now.AddHours(2), Now.AddHours(3), true, 0, "Planned"));
        db.Add(itinerary);
        await db.SaveChangesAsync();
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        var session = TripSession.Start(
            itinerary.Id, itinerary.Id, TravelerId, Guid.NewGuid(), Now, Now.AddHours(8),
            items.Select(item => TripSessionItem.Snapshot(
                item.Id, item.SequenceNo, poi.Id, poi.Name, poi.Latitude, poi.Longitude,
                item.PlannedArrivalUtc!.Value, item.PlannedDepartureUtc!.Value, true)));
        db.Add(session);
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();
        return new Seed(session.Id, items[0].Id, items[1].Id);
    }

    private sealed record Seed(long SessionId, long FirstItemId, long SecondItemId);
}
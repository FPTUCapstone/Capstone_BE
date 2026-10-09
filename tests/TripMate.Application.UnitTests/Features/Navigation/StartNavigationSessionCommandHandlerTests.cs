using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Navigation.Start;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Navigation;

public sealed class StartNavigationSessionCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 20, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_ActiveAccessibleItineraryCreatesFrozenNavigatingSession()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedActiveItineraryAsync(db, Now.AddHours(1));

        var result = await CreateHandler(db, Now).Handle(
            new StartNavigationSessionCommand(itinerary.Id, 7, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(TripSession.NavigatingState);
        result.Value.ExpiresAtUtc.Should().Be(Now.AddHours(2) + TimeSpan.FromHours(6));
        result.Value.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            PoiName = "Marble Mountains",
            Latitude = 16.003300m,
            Longitude = 108.263500m,
            PlannedArrivalUtc = Now.AddHours(1),
            PlannedDepartureUtc = Now.AddHours(2),
            IsMandatory = true,
            Status = TripSessionItem.PendingStatus,
        });
        (await db.TripSessions.CountAsync()).Should().Be(1);
        (await db.TripSessionItems.CountAsync()).Should().Be(1);
        (await db.TripStateHistory.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(-181)]
    [InlineData(7 * 60)]
    public async Task Handle_OutsideTheTripWindowRejectsWithoutCreatingRows(int minutesFromFirstArrival)
    {
        await using var db = CreateDbContext();
        var firstArrival = Now.AddMinutes(-minutesFromFirstArrival);
        var itinerary = await SeedActiveItineraryAsync(db, firstArrival);

        var result = await CreateHandler(db, Now).Handle(
            new StartNavigationSessionCommand(itinerary.Id, 7, Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(NavigationErrorCodes.OutsideTripWindow);
        (await db.TripSessions.CountAsync()).Should().Be(0);
        (await db.TripStateHistory.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(-180)]
    [InlineData((7 * 60) - 1)]
    public async Task Handle_AtTheTripWindowBoundariesStarts(int minutesFromFirstArrival)
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedActiveItineraryAsync(db, Now.AddMinutes(-minutesFromFirstArrival));

        var result = await CreateHandler(db, Now).Handle(
            new StartNavigationSessionCommand(itinerary.Id, 7, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ExpiredOpenSessionIsClosedThenTheNewSessionStarts()
    {
        await using var db = CreateDbContext();
        var previous = await SeedActiveItineraryAsync(db, Now.AddDays(-1));
        var stale = await SeedOpenSessionAsync(db, previous, Now.AddDays(-1));
        var current = await SeedItineraryAsync(db, Now.AddHours(1));

        var result = await CreateHandler(db, Now).Handle(
            new StartNavigationSessionCommand(current.Id, 7, Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var expired = await db.TripSessions
            .Include(session => session.StateHistory)
            .SingleAsync(session => session.Id == stale.Id);
        expired.FsmState.Should().Be(TripSession.CompletedState);
        expired.CompletionReason.Should().Be(TripSession.ExpiredReason);
        expired.EndedAtUtc.Should().Be(stale.ExpiresAtUtc);
        expired.StateHistory.Should().HaveCount(2);
        expired.StateHistory.Last().TriggeredBy.Should().Be(TripStateHistory.SystemTrigger);
        (await db.TripSessions.CountAsync(session => session.EndedAtUtc == null)).Should().Be(1);
    }

    [Fact]
    public async Task Handle_UnexpiredOpenSessionReturnsRecoveryMetadata()
    {
        await using var db = CreateDbContext();
        var previous = await SeedActiveItineraryAsync(db, Now.AddHours(-1));
        var open = await SeedOpenSessionAsync(db, previous, Now.AddHours(-1));
        var current = await SeedItineraryAsync(db, Now.AddHours(1));

        var result = await CreateHandler(db, Now).Handle(
            new StartNavigationSessionCommand(current.Id, 7, Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(NavigationErrorCodes.ActiveSessionExists);
        result.ErrorMetadata[NavigationErrorMetadata.ActiveSessionId].Should().Be(open.Id);
        result.ErrorMetadata[NavigationErrorMetadata.ActiveSessionLocation]
            .Should().Be($"/api/v1/navigation-sessions/{open.Id}");
        (await db.TripSessions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_UsesConfiguredWindows()
    {
        await using var db = CreateDbContext();
        var itinerary = await SeedActiveItineraryAsync(db, Now.AddHours(2));
        var options = new NavigationSessionOptions
        {
            EarlyStartWindow = TimeSpan.FromHours(1),
            ExpiryGracePeriod = TimeSpan.FromHours(1),
        };

        var tooEarly = await CreateHandler(db, Now, options).Handle(
            new StartNavigationSessionCommand(itinerary.Id, 7, Guid.NewGuid()),
            CancellationToken.None);
        var started = await CreateHandler(db, Now.AddHours(1), options).Handle(
            new StartNavigationSessionCommand(itinerary.Id, 7, Guid.NewGuid()),
            CancellationToken.None);

        tooEarly.ErrorCode.Should().Be(NavigationErrorCodes.OutsideTripWindow);
        started.IsSuccess.Should().BeTrue();
        started.Value.ExpiresAtUtc.Should().Be(Now.AddHours(4));
    }

    private static StartNavigationSessionCommandHandler CreateHandler(
        TestDbContext db,
        DateTimeOffset now,
        NavigationSessionOptions? options = null) =>
        new(
            db,
            new ItineraryAccessService(db),
            new FakeDateTimeProvider { UtcNow = now },
            options);

    private static TestDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static async Task<TripSession> SeedOpenSessionAsync(
        TestDbContext db,
        Itinerary itinerary,
        DateTimeOffset startedAtUtc)
    {
        var item = itinerary.Items.Single();
        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            7,
            Guid.NewGuid(),
            startedAtUtc,
            item.PlannedDepartureUtc + TimeSpan.FromHours(6),
            [
                TripSessionItem.Snapshot(
                    item.Id,
                    item.SequenceNo,
                    item.PointOfInterestId!.Value,
                    "Previous POI",
                    16m,
                    108m,
                    item.PlannedArrivalUtc,
                    item.PlannedDepartureUtc,
                    item.IsMandatory),
            ]);
        db.TripSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task<Itinerary> SeedActiveItineraryAsync(
        TestDbContext db,
        DateTimeOffset firstArrivalUtc)
    {
        db.Users.Add(new User
        {
            Id = 7,
            Email = "navigation@example.com",
            FullName = "Navigation Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });
        await db.SaveChangesAsync();
        return await SeedItineraryAsync(db, firstArrivalUtc);
    }

    private static async Task<Itinerary> SeedItineraryAsync(TestDbContext db, DateTimeOffset firstArrivalUtc)
    {
        var category = PoiCategory.Create($"Attraction {Guid.NewGuid():N}", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();
        var poi = PointOfInterest.Create(
            category,
            "Marble Mountains",
            16.003300m,
            108.263500m,
            7,
            Now);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(7, "Active navigation", Itinerary.ActiveStatus, Now);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1,
            poi.Id,
            firstArrivalUtc,
            firstArrivalUtc.AddHours(1),
            true,
            0m,
            "Required"));
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        return itinerary;
    }
}
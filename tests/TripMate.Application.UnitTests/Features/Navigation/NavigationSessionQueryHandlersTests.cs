using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Navigation.Get;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Navigation;

public sealed class NavigationSessionQueryHandlersTests
{
    private const long TravelerId = 7;

    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 10, 20, 1, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpiresAtUtc = StartedAtUtc.AddHours(12);

    [Fact]
    public async Task Get_AfterExpiryReturnsTheEffectiveStateWithoutWriting()
    {
        await using var db = CreateDbContext();
        var sessionId = await SeedAsync(db);

        var result = await new GetNavigationSessionQueryHandler(
                db,
                new ItineraryAccessService(db),
                Clock(ExpiresAtUtc.AddMinutes(1)))
            .Handle(new GetNavigationSessionQuery(sessionId, TravelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(TripSession.CompletedState);
        result.Value.CompletionReason.Should().Be(TripSession.RouteFinishedReason);
        result.Value.EndedAtUtc.Should().Be(ExpiresAtUtc);
        var stored = await db.TripSessions.AsNoTracking()
            .Include(session => session.StateHistory)
            .SingleAsync(session => session.Id == sessionId);
        stored.FsmState.Should().Be(TripSession.ExploringState);
        stored.EndedAtUtc.Should().BeNull();
        stored.StateHistory.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_OpenFilterReturnsAnExploringSessionUntilItExpires()
    {
        await using var db = CreateDbContext();
        var sessionId = await SeedAsync(db);

        var open = await List(db, StartedAtUtc.AddHours(1), ListNavigationSessionsQuery.OpenStateFilter);
        var expired = await List(db, ExpiresAtUtc, ListNavigationSessionsQuery.OpenStateFilter);

        open.IsSuccess.Should().BeTrue();
        open.Value.Should().ContainSingle().Which.SessionId.Should().Be(sessionId);
        open.Value.Single().State.Should().Be(TripSession.ExploringState);
        expired.IsSuccess.Should().BeTrue();
        expired.Value.Should().BeEmpty();
        (await db.TripSessions.AsNoTracking().SingleAsync()).EndedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("open")]
    [InlineData("Navigating")]
    public async Task List_RejectsAnyFilterOtherThanOpen(string? state)
    {
        await using var db = CreateDbContext();
        await SeedAsync(db);

        var result = await List(db, StartedAtUtc.AddHours(1), state);

        result.ErrorCode.Should().Be(NavigationErrorCodes.UnsupportedStateFilter);
    }

    private static Task<TripMate.Application.Common.Models.Result<IReadOnlyCollection<NavigationSessionResponse>>> List(
        TestDbContext db,
        DateTimeOffset now,
        string? state) =>
        new ListNavigationSessionsQueryHandler(db, new ItineraryAccessService(db), Clock(now))
            .Handle(new ListNavigationSessionsQuery(TravelerId, state), CancellationToken.None);

    private static FakeDateTimeProvider Clock(DateTimeOffset now) => new() { UtcNow = now };

    private static TestDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static async Task<long> SeedAsync(TestDbContext db)
    {
        db.Users.Add(new User
        {
            Id = TravelerId,
            Email = "navigation-query@example.com",
            FullName = "Navigation Query",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = StartedAtUtc,
            UpdatedAtUtc = StartedAtUtc,
        });
        var category = PoiCategory.Create("Navigation query", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();
        var poi = PointOfInterest.Create(category, "Dragon Bridge", 16m, 108m, TravelerId, StartedAtUtc);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(TravelerId, "Query", Itinerary.ActiveStatus, StartedAtUtc);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1,
            poi.Id,
            StartedAtUtc.AddHours(1),
            StartedAtUtc.AddHours(2),
            true,
            0m,
            "Planned"));
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        var item = itinerary.Items.Single();
        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            TravelerId,
            Guid.NewGuid(),
            StartedAtUtc,
            ExpiresAtUtc,
            [
                TripSessionItem.Snapshot(
                    item.Id,
                    1,
                    poi.Id,
                    poi.Name,
                    poi.Latitude,
                    poi.Longitude,
                    item.PlannedArrivalUtc!.Value,
                    item.PlannedDepartureUtc!.Value,
                    item.IsMandatory),
            ]);
        session.ReachItem(item.Id, StartedAtUtc.AddMinutes(30));
        db.TripSessions.Add(session);
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();
        return session.Id;
    }
}
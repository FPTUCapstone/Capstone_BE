using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Navigation.Complete;
using TripMate.Application.Features.Navigation.Depart;
using TripMate.Application.Features.Navigation.Reach;
using TripMate.Application.Features.Navigation.Skip;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Navigation;

public sealed class NavigationSessionMutationHandlersTests
{
    private const long TravelerId = 7;
    private const long OtherTravelerId = 8;

    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 10, 20, 1, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpiresAtUtc = StartedAtUtc.AddHours(12);

    [Fact]
    public async Task Reach_StoresAnInRangeDeviceTimeAndEntersExploring()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);
        var occurredAt = StartedAtUtc.AddMinutes(40);

        var result = await Reach(db, StartedAtUtc.AddHours(1), seed.SessionId, seed.ItemIds[2], occurredAt);

        result.IsSuccess.Should().BeTrue();
        result.Value.State.Should().Be(TripSession.ExploringState);
        result.Value.ExploringItemId.Should().Be(seed.ItemIds[2]);
        result.Value.NextItemId.Should().Be(seed.ItemIds[0]);
        var stored = await LoadAsync(db, seed.SessionId);
        stored.Items.Single(item => item.ItineraryItemId == seed.ItemIds[2]).ReachedAtUtc.Should().Be(occurredAt);
        stored.StateHistory.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reach_OutOfRangeDeviceTimeUsesTheServerClockAndReplayKeepsIt()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);
        var now = StartedAtUtc.AddHours(1);

        var first = await Reach(db, now, seed.SessionId, seed.ItemIds[0], StartedAtUtc.AddMinutes(-5));
        var replay = await Reach(db, now.AddMinutes(10), seed.SessionId, seed.ItemIds[0], null);

        first.Value.Items.First().ReachedAtUtc.Should().Be(now);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Items.First().ReachedAtUtc.Should().Be(now);
        (await LoadAsync(db, seed.SessionId)).StateHistory.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reach_RejectsMissingForeignInaccessibleUnknownAndCompletedSessions()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);
        var inaccessible = await SeedAsync(db, itineraryOwnerId: OtherTravelerId, travelerId: TravelerId, createUsers: false);
        var now = StartedAtUtc.AddHours(1);

        (await Reach(db, now, 999_999, seed.ItemIds[0], null)).ErrorCode
            .Should().Be(NavigationErrorCodes.SessionNotFound);
        (await Reach(db, now, seed.SessionId, seed.ItemIds[0], null, OtherTravelerId)).ErrorCode
            .Should().Be(NavigationErrorCodes.AccessDenied);
        (await Reach(db, now, inaccessible.SessionId, inaccessible.ItemIds[0], null)).ErrorCode
            .Should().Be(NavigationErrorCodes.AccessDenied);
        (await Reach(db, now, seed.SessionId, 999_999, null)).ErrorCode
            .Should().Be(NavigationErrorCodes.ItemNotNavigable);

        await Finish(db, now, seed.SessionId);
        (await Reach(db, now, seed.SessionId, seed.ItemIds[1], null)).ErrorCode
            .Should().Be(NavigationErrorCodes.SessionCompleted);
    }

    [Fact]
    public async Task Reach_AfterExpiryPersistsTheExpiryAndRejectsTheReach()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);

        var result = await Reach(db, ExpiresAtUtc.AddMinutes(1), seed.SessionId, seed.ItemIds[0], null);

        result.ErrorCode.Should().Be(NavigationErrorCodes.SessionCompleted);
        var stored = await LoadAsync(db, seed.SessionId);
        stored.FsmState.Should().Be(TripSession.CompletedState);
        stored.CompletionReason.Should().Be(TripSession.ExpiredReason);
        stored.EndedAtUtc.Should().Be(ExpiresAtUtc);
        stored.StateHistory.Last().TriggeredBy.Should().Be(TripStateHistory.TriggeredBySystem);
    }

    [Fact]
    public async Task Skip_SkipsAnyPendingItemAndRejectsReachedItems()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);
        var now = StartedAtUtc.AddHours(1);
        await Reach(db, now, seed.SessionId, seed.ItemIds[0], null);

        // The device time is after the recorded reach, so it is kept (see TripSessionTests for
        // device times older than the last recorded event).
        var skipped = await Skip(
            db,
            now.AddMinutes(10),
            seed.SessionId,
            seed.ItemIds[2],
            StartedAtUtc.AddMinutes(65));
        var replay = await Skip(db, now.AddMinutes(11), seed.SessionId, seed.ItemIds[2], null);
        var reached = await Skip(db, now, seed.SessionId, seed.ItemIds[0], null);

        skipped.IsSuccess.Should().BeTrue();
        skipped.Value.State.Should().Be(TripSession.ExploringState);
        skipped.Value.Items.Last().Status.Should().Be(TripSessionItem.SkippedStatus);
        skipped.Value.Items.Last().SkippedAtUtc.Should().Be(StartedAtUtc.AddMinutes(65));
        replay.Value.Items.Last().SkippedAtUtc.Should().Be(StartedAtUtc.AddMinutes(65));
        reached.ErrorCode.Should().Be(NavigationErrorCodes.ItemAlreadyReached);
    }

    [Fact]
    public async Task Depart_ReturnsToNavigatingOnceAndRejectsCompletedSessions()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);
        var now = StartedAtUtc.AddHours(1);
        await Reach(db, now, seed.SessionId, seed.ItemIds[0], null);

        var departed = await Depart(db, now.AddMinutes(30), seed.SessionId);
        var replay = await Depart(db, now.AddMinutes(31), seed.SessionId);

        departed.Value.State.Should().Be(TripSession.NavigatingState);
        departed.Value.ExploringItemId.Should().BeNull();
        replay.IsSuccess.Should().BeTrue();
        (await LoadAsync(db, seed.SessionId)).StateHistory.Should().HaveCount(3);

        await Finish(db, now.AddMinutes(40), seed.SessionId);
        (await Depart(db, now.AddMinutes(41), seed.SessionId)).ErrorCode
            .Should().Be(NavigationErrorCodes.SessionCompleted);
    }

    [Fact]
    public async Task Finish_IsAllowedAfterAccessRevocationAndReplayKeepsTheReason()
    {
        await using var db = CreateDbContext();
        var inaccessible = await SeedAsync(db, itineraryOwnerId: OtherTravelerId, travelerId: TravelerId);
        var now = StartedAtUtc.AddHours(1);

        var finished = await Finish(db, now, inaccessible.SessionId);
        var replay = await Finish(db, now.AddMinutes(5), inaccessible.SessionId);
        var foreign = await Finish(db, now, inaccessible.SessionId, OtherTravelerId);

        finished.Value.CompletionReason.Should().Be(TripSession.TravelerStoppedReason);
        finished.Value.EndedAtUtc.Should().Be(now);
        replay.Value.EndedAtUtc.Should().Be(now);
        foreign.ErrorCode.Should().Be(NavigationErrorCodes.AccessDenied);
        (await LoadAsync(db, inaccessible.SessionId)).StateHistory.Should().HaveCount(2);
    }

    [Fact]
    public async Task Finish_AfterExpiryReturnsThePersistedExpiry()
    {
        await using var db = CreateDbContext();
        var seed = await SeedAsync(db);

        var result = await Finish(db, ExpiresAtUtc.AddHours(1), seed.SessionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.CompletionReason.Should().Be(TripSession.ExpiredReason);
        result.Value.EndedAtUtc.Should().Be(ExpiresAtUtc);
    }

    [Fact]
    public void DepartValidator_AcceptsOnlyTheNavigatingTargetState()
    {
        var validator = new DepartNavigationSessionCommandValidator();

        validator.Validate(new DepartNavigationSessionCommand(1, TravelerId, TripSession.NavigatingState, null))
            .IsValid.Should().BeTrue();
        validator.Validate(new DepartNavigationSessionCommand(1, TravelerId, TripSession.CompletedState, null))
            .IsValid.Should().BeFalse();
        validator.Validate(new DepartNavigationSessionCommand(1, TravelerId, null, null))
            .IsValid.Should().BeFalse();
    }

    private static Task<Result<NavigationSessionResponse>> Reach(
        TestDbContext db,
        DateTimeOffset now,
        long sessionId,
        long itemId,
        DateTimeOffset? occurredAtUtc,
        long travelerId = TravelerId) =>
        new ReachNavigationItemCommandHandler(db, new ItineraryAccessService(db), Clock(now))
            .Handle(new ReachNavigationItemCommand(sessionId, itemId, travelerId, occurredAtUtc), CancellationToken.None);

    private static Task<Result<NavigationSessionResponse>> Skip(
        TestDbContext db,
        DateTimeOffset now,
        long sessionId,
        long itemId,
        DateTimeOffset? occurredAtUtc) =>
        new SkipNavigationItemCommandHandler(db, new ItineraryAccessService(db), Clock(now))
            .Handle(new SkipNavigationItemCommand(sessionId, itemId, TravelerId, occurredAtUtc), CancellationToken.None);

    private static Task<Result<NavigationSessionResponse>> Depart(
        TestDbContext db,
        DateTimeOffset now,
        long sessionId) =>
        new DepartNavigationSessionCommandHandler(db, new ItineraryAccessService(db), Clock(now))
            .Handle(
                new DepartNavigationSessionCommand(sessionId, TravelerId, TripSession.NavigatingState, null),
                CancellationToken.None);

    private static Task<Result<NavigationSessionResponse>> Finish(
        TestDbContext db,
        DateTimeOffset now,
        long sessionId,
        long travelerId = TravelerId) =>
        new CompleteNavigationSessionCommandHandler(db, Clock(now))
            .Handle(new CompleteNavigationSessionCommand(sessionId, travelerId), CancellationToken.None);

    private static FakeDateTimeProvider Clock(DateTimeOffset now) => new() { UtcNow = now };

    private static async Task<TripSession> LoadAsync(TestDbContext db, long sessionId)
    {
        db.ClearTrackedEntities();
        return await db.TripSessions
            .AsNoTracking()
            .Include(session => session.Items)
            .Include(session => session.StateHistory)
            .SingleAsync(session => session.Id == sessionId);
    }

    private static TestDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static async Task<(long SessionId, long[] ItemIds)> SeedAsync(
        TestDbContext db,
        long itineraryOwnerId = TravelerId,
        long travelerId = TravelerId,
        bool createUsers = true)
    {
        if (createUsers)
        {
            foreach (var userId in new[] { TravelerId, OtherTravelerId })
            {
                db.Users.Add(new User
                {
                    Id = userId,
                    Email = $"navigation-{userId}@example.com",
                    FullName = $"Navigation {userId}",
                    Role = UserRole.Traveler,
                    Status = AccountStatus.Active,
                    CreatedAtUtc = StartedAtUtc,
                    UpdatedAtUtc = StartedAtUtc,
                });
            }
        }

        var category = PoiCategory.Create($"Navigation {Guid.NewGuid():N}", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();
        var pois = Enumerable.Range(1, 3)
            .Select(index => PointOfInterest.Create(category, $"POI {index}", 16m, 108m, itineraryOwnerId, StartedAtUtc))
            .ToArray();
        db.PointsOfInterest.AddRange(pois);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(itineraryOwnerId, "Navigation", Itinerary.ActiveStatus, StartedAtUtc);
        for (var index = 0; index < pois.Length; index++)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                index + 1,
                pois[index].Id,
                StartedAtUtc.AddHours(index + 1),
                StartedAtUtc.AddHours(index + 2),
                index == 0,
                0m,
                "Planned"));
        }

        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            travelerId,
            Guid.NewGuid(),
            StartedAtUtc,
            ExpiresAtUtc,
            items.Select((item, index) => TripSessionItem.Snapshot(
                item.Id,
                item.SequenceNo,
                pois[index].Id,
                pois[index].Name,
                pois[index].Latitude,
                pois[index].Longitude,
                item.PlannedArrivalUtc!.Value,
                item.PlannedDepartureUtc!.Value,
                item.IsMandatory)));
        db.TripSessions.Add(session);
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();
        return (session.Id, items.Select(item => item.Id).ToArray());
    }
}
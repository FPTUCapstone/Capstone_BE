using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Navigation.Complete;
using TripMate.Application.Features.Navigation.Reach;
using TripMate.Application.Features.Navigation.Skip;
using TripMate.Application.Features.Navigation.Start;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Navigation;

public sealed class NavigationSessionSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Start_WhenSaveCompletionFails_RollsBackSessionItemsAndHistory()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, createSession: false);
        await using var db = database.CreateDbContext(new ThrowAfterSaveInterceptor());
        var handler = new StartNavigationSessionCommandHandler(
            db,
            new ItineraryAccessService(db),
            new FixedClock(seed.StartedAtUtc));

        Func<Task> start = async () => await handler.Handle(
            new StartNavigationSessionCommand(seed.ItineraryId, seed.TravelerId, Guid.NewGuid()),
            CancellationToken.None);

        await start.Should().ThrowAsync<InvalidOperationException>();
        await using var assertionDb = database.CreateDbContext();
        (await assertionDb.TripSessions.CountAsync()).Should().Be(0);
        (await assertionDb.TripSessionItems.CountAsync()).Should().Be(0);
        (await assertionDb.TripStateHistory.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentStartsAcrossContexts_CreateOnlyOneOpenSession()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, createSession: false);
        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();
        var firstHandler = new StartNavigationSessionCommandHandler(
            firstDb,
            new ItineraryAccessService(firstDb),
            new FixedClock(seed.StartedAtUtc));
        var secondHandler = new StartNavigationSessionCommandHandler(
            secondDb,
            new ItineraryAccessService(secondDb),
            new FixedClock(seed.StartedAtUtc));

        var results = await Task.WhenAll(
            firstHandler.Handle(
                new StartNavigationSessionCommand(seed.ItineraryId, seed.TravelerId, Guid.NewGuid()),
                CancellationToken.None),
            secondHandler.Handle(
                new StartNavigationSessionCommand(seed.ItineraryId, seed.TravelerId, Guid.NewGuid()),
                CancellationToken.None));

        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result =>
            result.IsFailure && result.ErrorCode == NavigationErrorCodes.ActiveSessionExists);
        await using var assertionDb = database.CreateDbContext();
        (await assertionDb.TripSessions.CountAsync(session =>
            session.TravelerUserId == seed.TravelerId && session.EndedAtUtc == null)).Should().Be(1);
        (await assertionDb.TripSessionItems.CountAsync()).Should().Be(1);
        (await assertionDb.TripStateHistory.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task StopWinsReach_RetriesAgainstTheCompletedSessionAndReturnsSessionCompleted()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var interceptor = new BlockingSaveChangesInterceptor();
        await using var reachDb = database.CreateDbContext(interceptor);
        await using var stopDb = database.CreateDbContext();
        var reachHandler = new ReachNavigationItemCommandHandler(
            reachDb,
            new ItineraryAccessService(reachDb),
            new FixedClock(seed.StartedAtUtc.AddMinutes(30)));
        var stopHandler = new CompleteNavigationSessionCommandHandler(
            stopDb,
            new FixedClock(seed.StartedAtUtc.AddMinutes(29)));

        var reachTask = reachHandler.Handle(
            new ReachNavigationItemCommand(seed.SessionId, seed.ItemIds.Single(), seed.TravelerId, null),
            CancellationToken.None);
        await interceptor.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var stopped = await stopHandler.Handle(
            new CompleteNavigationSessionCommand(seed.SessionId, seed.TravelerId),
            CancellationToken.None);
        stopped.IsSuccess.Should().BeTrue();
        stopped.Value.CompletionReason.Should().Be(TripSession.TravelerStoppedReason);

        interceptor.AllowSave.TrySetResult();
        var reached = await reachTask;

        reached.IsFailure.Should().BeTrue();
        reached.ErrorCode.Should().Be(NavigationErrorCodes.SessionCompleted);
        await using var assertionDb = database.CreateDbContext();
        var session = await assertionDb.TripSessions
            .AsNoTracking()
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.StateHistory)
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        session.Items.Single().ReachedAtUtc.Should().BeNull();
        session.StateHistory.Should().HaveCount(2);
        session.CompletionReason.Should().Be(TripSession.TravelerStoppedReason);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentDuplicateReach_RetriesAsReplayAndPreservesTheCommittedFirstTimestamp()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, itemCount: 2);
        var interceptor = new BlockingSaveChangesInterceptor();
        await using var blockedDb = database.CreateDbContext(interceptor);
        await using var winnerDb = database.CreateDbContext();
        var blockedTimestamp = seed.StartedAtUtc.AddMinutes(30);
        var winnerTimestamp = seed.StartedAtUtc.AddMinutes(31);
        var blockedHandler = new ReachNavigationItemCommandHandler(
            blockedDb,
            new ItineraryAccessService(blockedDb),
            new FixedClock(blockedTimestamp));
        var winnerHandler = new ReachNavigationItemCommandHandler(
            winnerDb,
            new ItineraryAccessService(winnerDb),
            new FixedClock(winnerTimestamp));

        var blockedTask = blockedHandler.Handle(
            new ReachNavigationItemCommand(seed.SessionId, seed.ItemIds[0], seed.TravelerId, null),
            CancellationToken.None);
        await interceptor.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var winner = await winnerHandler.Handle(
            new ReachNavigationItemCommand(seed.SessionId, seed.ItemIds[0], seed.TravelerId, null),
            CancellationToken.None);
        winner.IsSuccess.Should().BeTrue();

        interceptor.AllowSave.TrySetResult();
        var replay = await blockedTask;

        replay.IsSuccess.Should().BeTrue();
        replay.Value.Items.Single(item => item.ItemId == seed.ItemIds[0]).ReachedAtUtc
            .Should().Be(winnerTimestamp);
        await using var assertionDb = database.CreateDbContext();
        var session = await assertionDb.TripSessions
            .AsNoTracking()
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.StateHistory)
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        session.Items.Single(item => item.ItineraryItemId == seed.ItemIds[0]).ReachedAtUtc
            .Should().Be(winnerTimestamp);
        session.Items.Single(item => item.ItineraryItemId == seed.ItemIds[1]).ReachedAtUtc
            .Should().BeNull();
        session.LastSyncedAtUtc.Should().BeNull();
        session.FsmState.Should().Be(TripSession.ExploringState);
        session.ExploringItemId.Should().Be(seed.ItemIds[0]);
        session.StateHistory.Should().HaveCount(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSkipAndReachOfDifferentItems_BothSucceedAfterRetry()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, itemCount: 2);
        var interceptor = new BlockingSaveChangesInterceptor();
        await using var skipDb = database.CreateDbContext(interceptor);
        await using var reachDb = database.CreateDbContext();
        var skipHandler = new SkipNavigationItemCommandHandler(
            skipDb,
            new ItineraryAccessService(skipDb),
            new FixedClock(seed.StartedAtUtc.AddMinutes(20)));
        var reachHandler = new ReachNavigationItemCommandHandler(
            reachDb,
            new ItineraryAccessService(reachDb),
            new FixedClock(seed.StartedAtUtc.AddMinutes(21)));

        var skipTask = skipHandler.Handle(
            new SkipNavigationItemCommand(seed.SessionId, seed.ItemIds[1], seed.TravelerId, null),
            CancellationToken.None);
        await interceptor.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var reached = await reachHandler.Handle(
            new ReachNavigationItemCommand(seed.SessionId, seed.ItemIds[0], seed.TravelerId, null),
            CancellationToken.None);
        reached.IsSuccess.Should().BeTrue();

        interceptor.AllowSave.TrySetResult();
        var skipped = await skipTask;

        skipped.IsSuccess.Should().BeTrue();
        await using var assertionDb = database.CreateDbContext();
        var session = await assertionDb.TripSessions
            .AsNoTracking()
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.StateHistory)
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        session.Items.Single(item => item.ItineraryItemId == seed.ItemIds[0]).Status
            .Should().Be(TripSessionItem.ReachedStatus);
        session.Items.Single(item => item.ItineraryItemId == seed.ItemIds[1]).Status
            .Should().Be(TripSessionItem.SkippedStatus);
        session.FsmState.Should().Be(TripSession.ExploringState);
        session.StateHistory.Should().HaveCount(2);
    }

    private static async Task<SeedResult> SeedAsync(
        SqlServerTestDatabase database,
        int itemCount = 1,
        bool createSession = true)
    {
        await using var db = database.CreateDbContext();
        var startedAtUtc = new DateTimeOffset(2026, 10, 20, 1, 30, 0, TimeSpan.Zero);
        var user = new User
        {
            Email = $"uc13-{Guid.NewGuid():N}@example.com",
            FullName = "UC-13 SQL Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc,
        };
        db.Users.Add(user);
        var category = PoiCategory.Create($"UC13-{Guid.NewGuid():N}", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(
            user.Id,
            "SQL Navigation",
            Itinerary.ActiveStatus,
            startedAtUtc);
        var pois = new List<PointOfInterest>();
        for (var index = 0; index < itemCount; index++)
        {
            var poi = PointOfInterest.Create(
                category,
                $"SQL Navigation POI {index + 1}",
                16.061100m + (index * 0.001m),
                108.227600m + (index * 0.001m),
                user.Id,
                startedAtUtc);
            db.PointsOfInterest.Add(poi);
            pois.Add(poi);
        }

        await db.SaveChangesAsync();
        for (var index = 0; index < itemCount; index++)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                index + 1,
                pois[index].Id,
                startedAtUtc.AddHours(index + 1),
                startedAtUtc.AddHours(index + 2),
                true,
                0m,
                "Required"));
        }

        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        var orderedItems = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        long sessionId = 0;
        if (createSession)
        {
            var session = TripSession.Start(
                itinerary.Id,
                itinerary.Id,
                user.Id,
                Guid.NewGuid(),
                startedAtUtc,
                startedAtUtc.AddDays(1),
                orderedItems.Select((item, index) => TripSessionItem.Snapshot(
                    item.Id,
                    item.SequenceNo,
                    pois[index].Id,
                    pois[index].Name,
                    pois[index].Latitude,
                    pois[index].Longitude,
                    item.PlannedArrivalUtc,
                    item.PlannedDepartureUtc,
                    item.IsMandatory)));
            db.TripSessions.Add(session);
            await db.SaveChangesAsync();
            sessionId = session.Id;
        }

        return new SeedResult(
            user.Id,
            itinerary.Id,
            orderedItems.Select(item => item.Id).ToArray(),
            sessionId,
            startedAtUtc);
    }

    private sealed record SeedResult(
        long TravelerId,
        long ItineraryId,
        IReadOnlyList<long> ItemIds,
        long SessionId,
        DateTimeOffset StartedAtUtc);

    private sealed class FixedClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class BlockingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource SaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveEntered.TrySetResult();
            await AllowSave.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class ThrowAfterSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new InvalidOperationException("Forced post-save failure."));
    }
}
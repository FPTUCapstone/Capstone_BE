using System.Data.Common;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Common;
using TripMate.Application.Features.Navigation.Complete;
using TripMate.Application.Features.Navigation.Depart;
using TripMate.Application.Features.Navigation.Reach;
using TripMate.Application.Features.Navigation.Skip;
using TripMate.Application.Features.Navigation.Start;
using TripMate.Application.Features.TravelGroups.RemoveGroupMember;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Navigation;

public sealed class NavigationSessionSqlServerTests
{
    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("start")]
    [InlineData("reach")]
    [InlineData("skip")]
    [InlineData("depart")]
    public async Task Uc20Removal_AfterNavigationAccessCheck_CommitsAfterMutationAndEndsGroupSession(string action)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedGroupMemberAsync(database, createSession: action != "start", exploring: action == "depart");
        var accessRead = new BlockingReaderInterceptor("GroupMembers");
        await using var mutationDb = database.CreateDbContext(accessRead);
        var mutation = RunAction(action, mutationDb, seed);

        await accessRead.ReadExecuted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var removal = RemoveWithUc20Async(database, seed);
        var removalCommittedBeforeMutation = await Task.WhenAny(
            removal, Task.Delay(TimeSpan.FromSeconds(1))) == removal;

        accessRead.Resume.TrySetResult();
        var actionResult = await mutation.WaitAsync(TimeSpan.FromSeconds(30));
        var removalResult = await removal.WaitAsync(TimeSpan.FromSeconds(30));
        removalCommittedBeforeMutation.Should().BeFalse();
        actionResult.IsSuccess.Should().BeTrue();
        removalResult.IsSuccess.Should().BeTrue();

        await using var verification = database.CreateDbContext();
        (await verification.GroupMembers.SingleAsync(item => item.GroupId == seed.GroupId
            && item.UserId == seed.TravelerId)).Status.Should().Be(GroupMemberStatus.Removed);
        (await verification.TripSessions.SingleAsync(item => item.TravelerUserId == seed.TravelerId))
            .EndedAtUtc.Should().NotBeNull();
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await verification.Notifications.CountAsync()).Should().Be(1);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("start")]
    [InlineData("reach")]
    [InlineData("skip")]
    [InlineData("depart")]
    public async Task Uc20Removal_BeforeNavigationAccessCheck_DeniesMutation(string action)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedGroupMemberAsync(database, createSession: action != "start", exploring: action == "depart");
        var sessionRead = new BlockingReaderInterceptor("TripSessions");
        await using var mutationDb = database.CreateDbContext(sessionRead);
        var mutation = RunAction(action, mutationDb, seed);

        await sessionRead.ReadExecuted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var removalResult = await RemoveWithUc20Async(database, seed).WaitAsync(TimeSpan.FromSeconds(30));
        removalResult.IsSuccess.Should().BeTrue();
        sessionRead.Resume.TrySetResult();
        var actionResult = await mutation.WaitAsync(TimeSpan.FromSeconds(30));
        actionResult.IsFailure.Should().BeTrue();
        actionResult.ErrorCode.Should().Be(NavigationErrorCodes.AccessDenied);

        await using var verification = database.CreateDbContext();
        (await verification.GroupMembers.SingleAsync(item => item.GroupId == seed.GroupId
            && item.UserId == seed.TravelerId)).Status.Should().Be(GroupMemberStatus.Removed);
        (await verification.TripSessions.CountAsync(item => item.TravelerUserId == seed.TravelerId))
            .Should().Be(action == "start" ? 0 : 1);
        (await verification.GroupMemberRemovalOperations.CountAsync()).Should().Be(1);
        (await verification.Notifications.CountAsync()).Should().Be(1);
    }

    private static async Task<Result<RemoveGroupMemberResponse>> RemoveWithUc20Async(
        SqlServerTestDatabase database, GroupSeedResult seed)
    {
        await using var context = database.CreateDbContext();
        return await new RemoveGroupMemberCommandHandler(
            context, new FixedClock(seed.StartedAtUtc.AddMinutes(30)),
            new SqlServerGroupJoinLock(context), new ItineraryAccessService(context),
            NullLogger<RemoveGroupMemberCommandHandler>.Instance).Handle(
            new(seed.GroupId, seed.TravelerId, seed.OwnerId, Guid.NewGuid()), CancellationToken.None);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("start")]
    [InlineData("reach")]
    [InlineData("skip")]
    [InlineData("depart")]
    public async Task MembershipRevocation_AfterTheAccessCheckWaitsUntilTheMutationCommits(string action)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedGroupMemberAsync(database, createSession: action != "start", exploring: action == "depart");
        var accessRead = new BlockingReaderInterceptor("GroupMembers");
        await using var mutationDb = database.CreateDbContext(accessRead);

        var mutation = RunAction(action, mutationDb, seed);

        await accessRead.ReadExecuted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var revocationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revocation = RevokeMembershipAsync(database, seed.GroupId, seed.TravelerId, revocationStarted);
        await revocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var revocationCommittedBeforeMutation = await Task.WhenAny(
            revocation,
            Task.Delay(TimeSpan.FromSeconds(1))) == revocation;

        accessRead.Resume.TrySetResult();
        var result = await mutation.WaitAsync(TimeSpan.FromSeconds(15));
        await revocation.WaitAsync(TimeSpan.FromSeconds(15));

        revocationCommittedBeforeMutation.Should().BeFalse(
            "the access reads keep their locks until the navigation mutation commits");
        result.IsSuccess.Should().BeTrue();
        await using var assertionDb = database.CreateDbContext();
        (await assertionDb.GroupMembers.SingleAsync(member =>
            member.GroupId == seed.GroupId && member.UserId == seed.TravelerId)).Status
            .Should().Be(GroupMemberStatus.Removed);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("start")]
    [InlineData("reach")]
    [InlineData("skip")]
    [InlineData("depart")]
    public async Task MembershipRevocation_CommittedBeforeTheAccessCheckDeniesTheMutation(string action)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedGroupMemberAsync(database, createSession: action != "start", exploring: action == "depart");
        // Pauses inside the mutation after its first session read (the session load, or the
        // idempotency-key lookup for start), which precedes the in-transaction access check.
        var sessionRead = new BlockingReaderInterceptor("TripSessions");
        await using var mutationDb = database.CreateDbContext(sessionRead);

        var mutation = RunAction(action, mutationDb, seed);

        await sessionRead.ReadExecuted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var revocationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await RevokeMembershipAsync(database, seed.GroupId, seed.TravelerId, revocationStarted)
            .WaitAsync(TimeSpan.FromSeconds(5));
        sessionRead.Resume.TrySetResult();
        var result = await mutation.WaitAsync(TimeSpan.FromSeconds(15));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(NavigationErrorCodes.AccessDenied);
        await using var assertionDb = database.CreateDbContext();
        var sessions = await assertionDb.TripSessions
            .AsNoTracking()
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.StateHistory)
            .Where(candidate => candidate.TravelerUserId == seed.TravelerId)
            .ToListAsync();
        if (action == "start")
        {
            sessions.Should().BeEmpty();
            return;
        }

        var session = sessions.Single();
        session.FsmState.Should().Be(action == "depart" ? TripSession.ExploringState : TripSession.NavigatingState);
        session.StateHistory.Should().HaveCount(action == "depart" ? 2 : 1);
        session.Items.Single(item => item.ItineraryItemId == seed.ItemIds[0]).Status
            .Should().Be(action == "depart" ? TripSessionItem.ReachedStatus : TripSessionItem.PendingStatus);
    }

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
        (await assertionDb.TripStateHistories.CountAsync()).Should().Be(0);
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
        (await assertionDb.TripStateHistories.CountAsync()).Should().Be(1);
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
            new FixedClock(seed.StartedAtUtc.AddMinutes(29)),
            new ItineraryAccessService(stopDb));

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

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentReachesOfTheLastTwoItems_CloseTheRouteExactlyOnce()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, itemCount: 2);
        var interceptor = new BlockingSaveChangesInterceptor();
        await using var blockedDb = database.CreateDbContext(interceptor);
        await using var winnerDb = database.CreateDbContext();
        var blockedHandler = new ReachNavigationItemCommandHandler(
            blockedDb,
            new ItineraryAccessService(blockedDb),
            new FixedClock(seed.StartedAtUtc.AddMinutes(40)));
        var winnerHandler = new ReachNavigationItemCommandHandler(
            winnerDb,
            new ItineraryAccessService(winnerDb),
            new FixedClock(seed.StartedAtUtc.AddMinutes(30)));

        var blockedTask = blockedHandler.Handle(
            new ReachNavigationItemCommand(seed.SessionId, seed.ItemIds[1], seed.TravelerId, null),
            CancellationToken.None);
        await interceptor.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var winner = await winnerHandler.Handle(
            new ReachNavigationItemCommand(seed.SessionId, seed.ItemIds[0], seed.TravelerId, null),
            CancellationToken.None);
        winner.Value.State.Should().Be(TripSession.ExploringState);

        interceptor.AllowSave.TrySetResult();
        var final = await blockedTask;

        final.IsSuccess.Should().BeTrue();
        final.Value.State.Should().Be(TripSession.CompletedState);
        final.Value.CompletionReason.Should().Be(TripSession.RouteFinishedReason);
        await using var assertionDb = database.CreateDbContext();
        var session = await assertionDb.TripSessions
            .AsNoTracking()
            .Include(candidate => candidate.StateHistory)
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        session.CompletionReason.Should().Be(TripSession.RouteFinishedReason);
        session.EndedAtUtc.Should().Be(seed.StartedAtUtc.AddMinutes(40));
        session.StateHistory.Select(history => history.Reason).Should().Equal(
            TripStateHistory.NavigationStartedReason,
            TripStateHistory.ItemReachedReason,
            TripSession.RouteFinishedReason);
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
                    item.PlannedArrivalUtc!.Value,
                    item.PlannedDepartureUtc!.Value,
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

    private static async Task<GroupSeedResult> SeedGroupMemberAsync(
        SqlServerTestDatabase database,
        bool createSession,
        bool exploring)
    {
        await using var db = database.CreateDbContext();
        var startedAtUtc = new DateTimeOffset(2026, 10, 20, 1, 30, 0, TimeSpan.Zero);
        var owner = new User
        {
            Email = $"uc13-owner-{Guid.NewGuid():N}@example.com",
            FullName = "UC-13 Owner",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc,
        };
        var member = new User
        {
            Email = $"uc13-member-{Guid.NewGuid():N}@example.com",
            FullName = "UC-13 Member",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc,
        };
        db.Users.AddRange(owner, member);
        var category = PoiCategory.Create($"UC13-Race-{Guid.NewGuid():N}", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();

        var pois = Enumerable.Range(1, 2)
            .Select(index => PointOfInterest.Create(
                category,
                $"UC-13 Race POI {index}",
                16m + index,
                108m + index,
                owner.Id,
                startedAtUtc))
            .ToArray();
        db.PointsOfInterest.AddRange(pois);
        var request = SchedulingRequest.Create(
            owner.Id,
            Guid.NewGuid(),
            new string('r', SchedulingRequest.RequestHashLength),
            startedAtUtc.AddHours(1),
            "Asia/Ho_Chi_Minh",
            16m,
            108m,
            17m,
            109m,
            null,
            true,
            180,
            TransportMode.Car,
            10m,
            null,
            "[]",
            RestPreference.None,
            startedAtUtc);
        var generationOwner = Guid.NewGuid();
        request.ClaimGeneration(generationOwner, startedAtUtc.AddMinutes(1), startedAtUtc);
        request.CompleteGeneration(generationOwner, startedAtUtc);
        db.SchedulingRequests.Add(request);
        await db.SaveChangesAsync();

        var itinerary = Itinerary.CreateCspGenerated(
            request,
            "UC-13 Race Plan",
            startedAtUtc.AddHours(1),
            startedAtUtc.AddHours(4));
        for (var index = 0; index < pois.Length; index++)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                index + 1,
                pois[index].Id,
                startedAtUtc.AddHours(index + 1),
                startedAtUtc.AddHours(index + 2),
                false,
                0m,
                null));
        }

        itinerary.Accept(startedAtUtc);
        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();

        var group = TravelGroup.Create(itinerary.Id, owner.Id, "UC-13 Race Group", startedAtUtc);
        group.AddMember(GroupMember.CreateHost(group, owner.Id, startedAtUtc));
        group.AddMember(GroupMember.CreateMember(group, member.Id, startedAtUtc));
        db.TravelGroups.Add(group);
        await db.SaveChangesAsync();

        var orderedItems = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        long sessionId = 0;
        if (createSession)
        {
            var session = TripSession.Start(
                itinerary.Id,
                itinerary.Id,
                member.Id,
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
                    item.PlannedArrivalUtc!.Value,
                    item.PlannedDepartureUtc!.Value,
                    item.IsMandatory)));
            if (exploring)
            {
                session.ReachItem(orderedItems[0].Id, startedAtUtc.AddMinutes(10));
            }

            db.TripSessions.Add(session);
            await db.SaveChangesAsync();
            sessionId = session.Id;
        }

        return new GroupSeedResult(
            owner.Id,
            member.Id,
            itinerary.Id,
            group.Id,
            orderedItems.Select(item => item.Id).ToArray(),
            sessionId,
            startedAtUtc);
    }

    private static Task<Result<NavigationSessionResponse>> RunAction(
        string action,
        ApplicationDbContext db,
        GroupSeedResult seed) =>
        action switch
        {
            "start" => new StartNavigationSessionCommandHandler(
                    db,
                    new ItineraryAccessService(db),
                    new FixedClock(seed.StartedAtUtc))
                .Handle(
                    new StartNavigationSessionCommand(seed.ItineraryId, seed.TravelerId, Guid.NewGuid()),
                    CancellationToken.None),
            "reach" => new ReachNavigationItemCommandHandler(
                    db,
                    new ItineraryAccessService(db),
                    new FixedClock(seed.StartedAtUtc.AddMinutes(30)))
                .Handle(
                    new ReachNavigationItemCommand(seed.SessionId, seed.ItemIds[0], seed.TravelerId, null),
                    CancellationToken.None),
            "skip" => new SkipNavigationItemCommandHandler(
                    db,
                    new ItineraryAccessService(db),
                    new FixedClock(seed.StartedAtUtc.AddMinutes(30)))
                .Handle(
                    new SkipNavigationItemCommand(seed.SessionId, seed.ItemIds[0], seed.TravelerId, null),
                    CancellationToken.None),
            "depart" => new DepartNavigationSessionCommandHandler(
                    db,
                    new ItineraryAccessService(db),
                    new FixedClock(seed.StartedAtUtc.AddMinutes(30)))
                .Handle(
                    new DepartNavigationSessionCommand(
                        seed.SessionId,
                        seed.TravelerId,
                        TripSession.NavigatingState,
                        null),
                    CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private static async Task RevokeMembershipAsync(
        SqlServerTestDatabase database,
        long groupId,
        long travelerId,
        TaskCompletionSource started)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = """
            UPDATE social.GroupMembers
            SET status = 'Removed', left_at = SYSUTCDATETIME()
            WHERE group_id = @groupId AND user_id = @travelerId;
            """;
        command.Parameters.AddWithValue("@groupId", groupId);
        command.Parameters.AddWithValue("@travelerId", travelerId);
        started.TrySetResult();
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SeedResult(
        long TravelerId,
        long ItineraryId,
        IReadOnlyList<long> ItemIds,
        long SessionId,
        DateTimeOffset StartedAtUtc);

    private sealed record GroupSeedResult(
        long OwnerId,
        long TravelerId,
        long ItineraryId,
        long GroupId,
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

    /// <summary>Pauses the first query that reads <paramref name="table"/> until resumed.</summary>
    private sealed class BlockingReaderInterceptor(string table) : DbCommandInterceptor
    {
        private int _blocked;

        public TaskCompletionSource ReadExecuted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"[{table}]", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                ReadExecuted.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }

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
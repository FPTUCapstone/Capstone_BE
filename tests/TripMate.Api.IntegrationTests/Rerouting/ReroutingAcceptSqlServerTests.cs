using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Rerouting.Accept;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Engine;
using TripMate.Application.Features.Rerouting.Generate;
using TripMate.Application.Features.Rerouting.Reject;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Rerouting;

/// <summary>
/// UC-15 T7 on real SQL Server. The in-memory provider enforces neither the foreign keys nor the unique index, so the
/// five-save order of <see cref="RerouteApplier"/> can only be proven here.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class ReroutingAcceptSqlServerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProposedAt = Start.AddMinutes(30);
    private static readonly DateTimeOffset AcceptedAt = ProposedAt.AddMinutes(5);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Accept_ReplacesPendingRowsThatReuseSequenceNumbers_WithoutForeignKeyOrIndexErrors()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedFourStopsAsync(database);

        var result = await AcceptAsync(database, seed, AcceptedAt);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        await using var verify = database.CreateDbContext();
        var versions = await verify.Itineraries.Include(itinerary => itinerary.Items)
            .Where(itinerary => itinerary.SchedulingRequestId == seed.RequestId).OrderBy(itinerary => itinerary.Version).ToListAsync();
        versions.Should().HaveCount(2);
        versions[0].Items.Should().HaveCount(4, "the previous version is retained untouched");
        versions[1].Items.OrderBy(item => item.SequenceNo).Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId).Should().Equal(seed.A, seed.B, seed.D);
        versions[1].Items.Select(item => item.SequenceNo).Should().OnlyHaveUniqueItems();

        var rows = await verify.TripSessionItems.Where(row => row.SessionId == seed.SessionId).OrderBy(row => row.SequenceNo).ToListAsync();
        rows.Select(row => row.PoiId).Should().Equal(seed.A, seed.B, seed.D);
        // The new rows reuse the numbers 2 and 3 that the deleted rows held, which is why S3 must precede S4.
        rows.Select(row => row.SequenceNo).Should().Equal(1, 2, 3);
        rows[0].ItineraryItemId.Should().Be(seed.ReachedItemId);
        rows[0].Status.Should().Be(TripSessionItem.ReachedStatus);

        var session = await verify.TripSessions.SingleAsync(candidate => candidate.Id == seed.SessionId);
        session.ItineraryId.Should().Be(versions[1].Id);
        var droppedAlert = await verify.Incidents.SingleAsync(incident => incident.Id == seed.DroppedStopIncidentId);
        droppedAlert.ResolvedAtUtc.Should().Be(AcceptedAt);
        droppedAlert.AffectedItineraryItemId.Should().BeNull();
        var keptAlert = await verify.Incidents.SingleAsync(incident => incident.Id == seed.KeptStopIncidentId);
        keptAlert.ResolvedAtUtc.Should().Be(AcceptedAt);
        keptAlert.AffectedItineraryItemId.Should().Be(rows[1].ItineraryItemId);
        var resolvedEarlier = await verify.Incidents.SingleAsync(incident => incident.Id == seed.ResolvedIncidentId);
        resolvedEarlier.ResolvedAtUtc.Should().Be(AcceptedAt.AddHours(-3), "an incident already resolved keeps its time");
        resolvedEarlier.AffectedItineraryItemId.Should().Be(rows[2].ItineraryItemId);
        var proposal = await verify.ReroutingEvents.SingleAsync(candidate => candidate.Id == seed.ProposalId);
        proposal.Status.Should().Be(ReroutingEvent.StatusAccepted);
        proposal.DecidedAtUtc.Should().Be(AcceptedAt);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Accept_TwoParallelRequests_CreateExactlyOneVersion()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedFourStopsAsync(database);

        var results = await Task.WhenAll(
            AcceptAsync(database, seed, AcceptedAt),
            AcceptAsync(database, seed, AcceptedAt));

        results.Should().OnlyContain(result => result.IsSuccess);
        results[0].Value.ItineraryId.Should().Be(results[1].Value.ItineraryId);
        await using var verify = database.CreateDbContext();
        (await verify.Itineraries.CountAsync(itinerary => itinerary.SchedulingRequestId == seed.RequestId)).Should().Be(2);
        (await verify.TripSessions.Include(session => session.StateHistory).SingleAsync(session => session.Id == seed.SessionId))
            .StateHistory.Count(history => history.Reason == TripStateHistory.RerouteAcceptedReason).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Accept_AfterReject_IsRefusedAndNothingChanges()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedFourStopsAsync(database);
        await using (var context = database.CreateDbContext())
        {
            var rejected = await new RejectReroutingProposalCommandHandler(
                    context,
                    new FixedClock(ProposedAt.AddMinutes(1)),
                    Options.Create(new ReroutingOptions()),
                    new SqlServerItineraryMutationLock(context))
                .Handle(new RejectReroutingProposalCommand(seed.SessionId, seed.ProposalId, seed.TravelerId), CancellationToken.None);
            rejected.IsSuccess.Should().BeTrue();
        }

        var result = await AcceptAsync(database, seed, AcceptedAt);

        result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalClosed);
        await AssertNothingChangedAsync(database, seed, ReroutingEvent.StatusRejected);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Accept_PastTheTtl_ExpiresTheProposalAndChangesNothing()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedFourStopsAsync(database);

        var result = await AcceptAsync(database, seed, ProposedAt.AddMinutes(15).AddSeconds(1));

        result.ErrorCode.Should().Be(ReroutingErrorCodes.ProposalExpired);
        await AssertNothingChangedAsync(database, seed, ReroutingEvent.StatusExpired);
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData(4, "the new rows were inserted but the incidents were not repointed yet")]
    [InlineData(5, "the proposal was accepted and the session moved, only the last repoint failed")]
    public async Task Accept_WhenALaterSaveFails_RollsBackEveryEarlierSave(int failingSave, string because)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedFourStopsAsync(database);

        var act = async () =>
        {
            var failure = new FailOnNthSave(failingSave);
            await using var context = database.CreateDbContext(failure);
            return await CreateAcceptHandler(context, AcceptedAt)
                .Handle(new AcceptReroutingProposalCommand(seed.SessionId, seed.ProposalId, seed.TravelerId), CancellationToken.None);
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*injected*", because);
        await AssertNothingChangedAsync(database, seed, ReroutingEvent.StatusProposed);
    }

    /// <summary>
    /// Why the applier orders its saves: these two constraints really exist in the schema the tests run on. If a later
    /// migration drops either of them, the order in <see cref="RerouteApplier"/> may be revisited.
    /// </summary>
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Schema_RefusesToDeleteASessionRowThatAnIncidentStillPointsAt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedFourStopsAsync(database);

        var act = () => database.ExecuteNonQueryAsync(
            $"DELETE trip.TripSessionItems WHERE session_id = {seed.SessionId} AND itinerary_item_id = {seed.BItemId};");

        (await act.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>())
            .Which.Number.Should().Be(547, "FK_Incidents_AffectedTripSessionItem is NoAction");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Schema_RefusesTwoSessionRowsWithTheSameSequenceNumber()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedFourStopsAsync(database);

        // The first reachable duplicate: give the row after A the sequence number of A.
        var act = () => database.ExecuteNonQueryAsync(
            $"UPDATE trip.TripSessionItems SET sequence_no = 1 WHERE session_id = {seed.SessionId} AND itinerary_item_id = {seed.BItemId};");

        (await act.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>())
            .Which.Number.Should().BeOneOf(2601, 2627);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task AssertNothingChangedAsync(SqlServerTestDatabase database, Seed seed, string expectedProposalStatus)
    {
        await using var verify = database.CreateDbContext();
        (await verify.Itineraries.CountAsync(itinerary => itinerary.SchedulingRequestId == seed.RequestId)).Should().Be(1);
        var session = await verify.TripSessions.Include(candidate => candidate.Items).Include(candidate => candidate.StateHistory)
            .SingleAsync(candidate => candidate.Id == seed.SessionId);
        session.ItineraryId.Should().Be(seed.ItineraryId);
        session.Items.Select(item => item.ItineraryItemId).Should().BeEquivalentTo(seed.AllItemIds);
        session.StateHistory.Should().NotContain(history => history.Reason == TripStateHistory.RerouteAcceptedReason);
        var droppedAlert = await verify.Incidents.SingleAsync(incident => incident.Id == seed.DroppedStopIncidentId);
        droppedAlert.ResolvedAtUtc.Should().BeNull();
        droppedAlert.AffectedItineraryItemId.Should().Be(seed.CItemId);
        var keptAlert = await verify.Incidents.SingleAsync(incident => incident.Id == seed.KeptStopIncidentId);
        keptAlert.ResolvedAtUtc.Should().BeNull();
        keptAlert.AffectedItineraryItemId.Should().Be(seed.BItemId);
        (await verify.ReroutingEvents.SingleAsync(proposal => proposal.Id == seed.ProposalId)).Status.Should().Be(expectedProposalStatus);
    }

    private static Task<TripMate.Application.Common.Models.Result<TripMate.Application.Features.Navigation.Common.NavigationSessionResponse>>
        AcceptAsync(SqlServerTestDatabase database, Seed seed, DateTimeOffset now) => Task.Run(async () =>
        {
            await using var context = database.CreateDbContext();
            return await CreateAcceptHandler(context, now).Handle(
                new AcceptReroutingProposalCommand(seed.SessionId, seed.ProposalId, seed.TravelerId), CancellationToken.None);
        });

    private static AcceptReroutingProposalCommandHandler CreateAcceptHandler(ApplicationDbContext context, DateTimeOffset now) =>
        new(
            context,
            new FixedClock(now),
            Options.Create(new ReroutingOptions()),
            new ItineraryVersionService(context, new FixedRouteDurationProvider()),
            new ItineraryAccessService(context),
            new SqlServerItineraryMutationLock(context));

    private sealed record Seed(
        long TravelerId,
        long RequestId,
        long ItineraryId,
        long SessionId,
        long ProposalId,
        long A,
        long B,
        long D,
        long ReachedItemId,
        long BItemId,
        long CItemId,
        long DroppedStopIncidentId,
        long KeptStopIncidentId,
        long ResolvedIncidentId,
        long[] AllItemIds);

    /// <summary>
    /// Stops A, B, C, D; A is reached and left. A severe-weather incident on C (a reviewed Outdoor stop) produced a stored
    /// Drop proposal [B, D]. Incident on B (kept) is open; an incident on D (kept) was resolved earlier; both still point
    /// at Pending rows, which is what blocks the delete.
    /// </summary>
    private static async Task<Seed> SeedFourStopsAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var user = new User
        {
            Email = $"uc15-sql-{Guid.NewGuid():N}@example.com",
            FullName = "UC15 SQL Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Start,
            UpdatedAtUtc = Start,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var category = PoiCategory.Create($"UC15 Culture {Guid.NewGuid():N}", null);
        var pois = new[]
        {
            CreatePoi(category, user.Id, "UC15 Stop A", 16.0471m, IndoorOutdoorType.Outdoor, reviewed: false),
            CreatePoi(category, user.Id, "UC15 Stop B", 16.0481m, IndoorOutdoorType.Outdoor, reviewed: false),
            CreatePoi(category, user.Id, "UC15 Stop C", 16.0491m, IndoorOutdoorType.Outdoor, reviewed: true),
            CreatePoi(category, user.Id, "UC15 Stop D", 16.0501m, IndoorOutdoorType.Outdoor, reviewed: false),
        };
        context.PointsOfInterest.AddRange(pois);
        await context.SaveChangesAsync();

        var request = SchedulingRequest.Create(
            user.Id, Guid.NewGuid(), new string('a', SchedulingRequest.RequestHashLength), Start, "Asia/Ho_Chi_Minh",
            16.0471m, 108.2068m, 16.0471m, 108.2068m, null, true, 480, TransportMode.Motorbike, 10m, 800_000m, "[]",
            RestPreference.None, Start.AddHours(-1));
        var owner = Guid.NewGuid();
        request.ClaimGeneration(owner, Start.AddMinutes(-59), Start.AddHours(-1));
        request.CompleteGeneration(owner, Start.AddHours(-1));
        context.SchedulingRequests.Add(request);
        await context.SaveChangesAsync();

        var itinerary = Itinerary.CreateCspGenerated(request, "UC15 SQL trip", Start, Start.AddHours(8));
        for (var index = 0; index < pois.Length; index++)
        {
            var arrival = Start.AddMinutes(10 + index * 70);
            itinerary.AddItem(ItineraryItem.CreateVisit(
                index + 1, pois[index].Id, arrival, arrival.AddMinutes(60), false, 60_000m, "Suggested nearby location",
                travelDurationToNextMinutes: index == pois.Length - 1 ? null : 10,
                travelDistanceToNextMeters: index == pois.Length - 1 ? null : 4000));
        }

        itinerary.Accept(Start);
        context.Itineraries.Add(itinerary);
        await context.SaveChangesAsync();

        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        var session = TripSession.Start(
            itinerary.Id, itinerary.Id, user.Id, Guid.NewGuid(), Start, Start.AddHours(12),
            items.Select((item, index) => TripSessionItem.Snapshot(
                item.Id, item.SequenceNo, pois[index].Id, pois[index].Name, pois[index].Latitude, pois[index].Longitude,
                item.PlannedArrivalUtc!.Value, item.PlannedDepartureUtc!.Value, false)));
        session.ReachItem(items[0].Id, Start.AddMinutes(15));
        session.Depart(Start.AddMinutes(20));
        context.TripSessions.Add(session);
        await context.SaveChangesAsync();

        var droppedAlert = Incident.CreateTripAlert(session, Incident.SevereWeatherType, $"tomorrow:thunderstorm:{items[2].Id}", ProposedAt, items[2].Id);
        var keptAlert = Incident.CreateTripAlert(session, Incident.ScheduleDelayType, $"delay:{items[1].Id}", ProposedAt, items[1].Id);
        var resolvedAlert = Incident.CreateTripAlert(session, Incident.PoiClosureType, $"closure:{items[3].Id}", ProposedAt.AddHours(-4), items[3].Id);
        resolvedAlert.Resolve(AcceptedAt.AddHours(-3));
        context.Incidents.AddRange(droppedAlert, keptAlert, resolvedAlert);
        await context.SaveChangesAsync();

        var options = Options.Create(new ReroutingOptions());
        var generator = new ReroutingProposalGenerator(
            context,
            new ReroutingProposalBuilder(context, new ItineraryVersionService(context, new FixedRouteDurationProvider()), options),
            options);
        (await generator.TryGenerateAsync(droppedAlert.Id, ProposedAt, CancellationToken.None)).Should().BeTrue();
        var proposal = await context.ReroutingEvents.SingleAsync();
        ReroutingProposalSnapshot.TryParse(proposal.ProposedItinerarySnapshot)!.Strategy.Should().Be(ReroutingStrategies.Drop);

        return new Seed(
            user.Id, request.Id, itinerary.Id, session.Id, proposal.Id, pois[0].Id, pois[1].Id, pois[3].Id,
            items[0].Id, items[1].Id, items[2].Id, droppedAlert.Id, keptAlert.Id, resolvedAlert.Id,
            items.Select(item => item.Id).ToArray());
    }

    private static PointOfInterest CreatePoi(
        PoiCategory category,
        long userId,
        string name,
        decimal latitude,
        IndoorOutdoorType indoorOutdoor,
        bool reviewed)
    {
        var poi = PointOfInterest.Create(
            category, name, latitude, 108.2068m, userId, Start, indoorOutdoor: indoorOutdoor, averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, "https://example.com/uc15", Start);
        poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
        if (reviewed)
        {
            poi.ReviewIndoorOutdoor(indoorOutdoor, Start);
        }

        return poi;
    }

    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Throws from the Nth SaveChanges of the context, after the earlier saves already ran inside the transaction.</summary>
    private sealed class FailOnNthSave(int failingSave) : SaveChangesInterceptor
    {
        private int _saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (++_saves == failingSave)
            {
                throw new InvalidOperationException("injected save failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class FixedRouteDurationProvider : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            var durations = new int[points.Count, points.Count];
            var meters = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    durations[row, column] = row == column ? 0 : 10;
                    meters[row, column] = row == column ? 0 : 4000;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations, unreachable: null, meters: meters));
        }
    }
}
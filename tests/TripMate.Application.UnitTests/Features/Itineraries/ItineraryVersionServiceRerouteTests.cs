using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

/// <summary>UC-15 T3: evaluating the remaining route from an anchor, and saving a rerouted version.</summary>
public sealed class ItineraryVersionServiceRerouteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    // The scheduling request starts at Now + 1h (Tuesday 09:00 Asia/Ho_Chi_Minh, POIs open 07:00-20:00 on Tuesday).
    private static readonly DateTimeOffset RequestStart = Now.AddHours(1);

    private const decimal CenterLatitude = 16.0471m;
    private const decimal CenterLongitude = 108.2068m;

    // About 20 km north of the trip centre, outside the request's 10 km search radius.
    private const decimal FarLatitude = 16.2271m;

    [Fact]
    public async Task Evaluate_WithoutAnchor_SchedulesFromTheRequestStart()
    {
        await using var fixture = await Fixture.CreateAsync();

        var evaluation = await fixture.EvaluateAsync([fixture.Museum.Id], anchor: null);

        evaluation.Plan.Should().NotBeNull();
        FirstVisitArrival(evaluation).Should().BeOnOrAfter(RequestStart);
        fixture.Routing.Calls[0][0].Should().BeEquivalentTo(new RoutePoint(CenterLatitude, CenterLongitude));
    }

    [Fact]
    public async Task Evaluate_WithAnchor_SchedulesFromTheAnchorTimeAndPoint()
    {
        await using var fixture = await Fixture.CreateAsync();
        var baseline = await fixture.EvaluateAsync([fixture.Museum.Id], anchor: null);
        var travelAndBuffer = FirstVisitArrival(baseline) - RequestStart;
        var anchorStart = RequestStart.AddHours(3);
        var anchor = new EvaluationAnchor(
            anchorStart,
            new RoutePoint(16.0612m, 108.2277m),
            AvailableMinutes: 300,
            RemainingBudgetVnd: 700_000m,
            CandidateRadiusKm: 5m,
            CandidateCenter: new RoutePoint(16.0612m, 108.2277m));
        fixture.Routing.Calls.Clear();

        var evaluation = await fixture.EvaluateAsync([fixture.Museum.Id], anchor);

        evaluation.Plan.Should().NotBeNull();
        FirstVisitArrival(evaluation).Should().Be(anchorStart + travelAndBuffer);
        fixture.Routing.Calls[0][0].Should().BeEquivalentTo(new RoutePoint(16.0612m, 108.2277m));
    }

    [Fact]
    public async Task Evaluate_WithAnchor_KeepsAVariantPoiOutsideTheTripRadius()
    {
        await using var fixture = await Fixture.CreateAsync();
        var far = await fixture.AddPoiAsync("Far gallery", FarLatitude);

        var withoutAnchor = await fixture.EvaluateAsync([far.Id], anchor: null);
        var withAnchor = await fixture.EvaluateAsync(
            [far.Id],
            new EvaluationAnchor(
                RequestStart.AddHours(1),
                new RoutePoint(FarLatitude, CenterLongitude),
                300,
                null,
                5m,
                new RoutePoint(FarLatitude, CenterLongitude)));

        withoutAnchor.Infeasibility.Should().NotBeNull();
        withoutAnchor.Infeasibility!.Reason.Should().Be(StopInfeasibilityReasons.OutsideRadius);
        withAnchor.Plan.Should().NotBeNull("the anchor radius, not the trip radius, bounds the candidates");
    }

    [Fact]
    public async Task Evaluate_WithAnchor_UsesRemainingBudgetInsteadOfTheRequestBudget()
    {
        await using var fixture = await Fixture.CreateAsync();
        var anchor = Anchor(RequestStart.AddHours(1), remainingBudgetVnd: 10_000m);

        var evaluation = await fixture.EvaluateAsync([fixture.Museum.Id], anchor);

        evaluation.Plan.Should().BeNull();
        evaluation.Infeasibility.Should().NotBeNull();
        evaluation.Infeasibility!.Reason.Should().Be(StopInfeasibilityReasons.ExceedsBudget);
    }

    [Fact]
    public async Task Evaluate_WithAnchor_NullBudgetMeansNoBudgetLimit()
    {
        await using var fixture = await Fixture.CreateAsync();

        var evaluation = await fixture.EvaluateAsync(
            [fixture.Museum.Id, fixture.Bridge.Id],
            Anchor(RequestStart.AddHours(1), remainingBudgetVnd: null));

        evaluation.Plan.Should().NotBeNull();
    }

    [Fact]
    public async Task Evaluate_WithAnchor_IsInfeasibleWhenTheStopsCannotEndByTheDayEnd()
    {
        await using var fixture = await Fixture.CreateAsync();

        var evaluation = await fixture.EvaluateAsync(
            [fixture.Museum.Id, fixture.Bridge.Id],
            Anchor(RequestStart.AddHours(1), availableMinutes: 5));

        evaluation.Plan.Should().BeNull();
        evaluation.Infeasibility.Should().NotBeNull();
        evaluation.Infeasibility!.Reason.Should().Be(StopInfeasibilityReasons.ExceedsEndTime);
    }

    [Fact]
    public async Task Evaluate_WithAnchorWithoutTimeLeft_FailsTheWholeEvaluation()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.EvaluateStopVariantsAsync(
            fixture.Request,
            [[new ItineraryStop(fixture.Museum.Id)]],
            CancellationToken.None,
            anchor: Anchor(RequestStart.AddHours(1), availableMinutes: 0));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ItineraryErrorCodes.ConstraintsInfeasible);
    }

    [Fact]
    public async Task CreateReroutedVersion_KeepsHistoryAndContinuesTheSequence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.AddItineraryAsync(version: 2, fixture.Museum.Id, fixture.Bridge.Id, fixture.Park.Id);
        var kept = source.Items.Where(item => item.PointOfInterestId == fixture.Museum.Id).ToArray();
        var evaluation = await fixture.EvaluateAsync([fixture.Park.Id], Anchor(RequestStart.AddHours(2)));
        var now = RequestStart.AddHours(2);

        var result = await fixture.Service.CreateReroutedVersionAsync(
            source, fixture.Request, kept, evaluation.Plan!, firstNewSequenceNo: 2, now, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var successor = result.Value;
        successor.Version.Should().Be(3);
        successor.Status.Should().Be(Itinerary.ActiveStatus);
        successor.SchedulingRequestId.Should().Be(fixture.Request.Id);
        successor.SolverOutcome.Should().Be(RerouteProvenance.SolverOutcome);
        successor.ValidFromUtc.Should().Be(source.ValidFromUtc);
        successor.ValidToUtc.Should().Be(evaluation.Plan!.EndAtUtc);
        successor.Items.Should().HaveCount(1 + evaluation.Plan.Items.Count);
        var ordered = successor.Items.OrderBy(item => item.SequenceNo).ToArray();
        ordered[0].PointOfInterestId.Should().Be(fixture.Museum.Id);
        ordered[0].SequenceNo.Should().Be(1);
        ordered[0].PlannedArrivalUtc.Should().Be(kept[0].PlannedArrivalUtc);
        ordered[1].SequenceNo.Should().Be(2);
        ordered.Select(item => item.SequenceNo).Should().OnlyHaveUniqueItems();
        ordered.Skip(1).Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId).Should().Equal(fixture.Park.Id);
        fixture.Db.Entry(successor).State.Should().Be(EntityState.Added);
    }

    [Fact]
    public async Task CreateReroutedVersion_LeavesTheSourceVersionUntouched()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.AddItineraryAsync(version: 2, fixture.Museum.Id, fixture.Bridge.Id);
        var evaluation = await fixture.EvaluateAsync([fixture.Park.Id], Anchor(RequestStart.AddHours(2)));

        _ = await fixture.Service.CreateReroutedVersionAsync(
            source,
            fixture.Request,
            source.Items.Where(item => item.PointOfInterestId == fixture.Museum.Id).ToArray(),
            evaluation.Plan!,
            2,
            RequestStart.AddHours(2),
            CancellationToken.None);

        source.Version.Should().Be(2);
        source.Items.Should().HaveCount(2);
        source.Status.Should().Be(Itinerary.DraftStatus, "the source is neither modified nor superseded in place");
        fixture.Db.Entry(source).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task CreateReroutedVersion_RejectsASequenceThatDoesNotFollowTheKeptItems()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.AddItineraryAsync(version: 1, fixture.Museum.Id, fixture.Bridge.Id);
        var evaluation = await fixture.EvaluateAsync([fixture.Park.Id], Anchor(RequestStart.AddHours(2)));
        var kept = source.Items.ToArray();

        var act = () => fixture.Service.CreateReroutedVersionAsync(
            source, fixture.Request, kept, evaluation.Plan!, firstNewSequenceNo: 2, RequestStart, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        fixture.Db.ChangeTracker.Entries<Itinerary>().Should().NotContain(entry => entry.State == EntityState.Added);
    }

    [Fact]
    public async Task CreateReroutedVersion_MarksARequestMandatoryPoiAsMandatory()
    {
        await using var fixture = await Fixture.CreateAsync(mandatoryPark: true);
        var source = await fixture.AddItineraryAsync(version: 1, fixture.Museum.Id, fixture.Park.Id);
        var evaluation = await fixture.EvaluateAsync([fixture.Park.Id], Anchor(RequestStart.AddHours(2)));

        var result = await fixture.Service.CreateReroutedVersionAsync(
            source,
            fixture.Request,
            source.Items.Where(item => item.PointOfInterestId == fixture.Museum.Id).ToArray(),
            evaluation.Plan!,
            2,
            RequestStart.AddHours(2),
            CancellationToken.None);

        result.Value.Items.Single(item => item.PointOfInterestId == fixture.Park.Id).IsMandatory.Should().BeTrue();
    }

    private static DateTimeOffset FirstVisitArrival(StopsEvaluation evaluation) =>
        evaluation.Plan!.Items.First(item => item.Kind == ItineraryItemKind.Visit).PlannedArrivalUtc;

    private static EvaluationAnchor Anchor(
        DateTimeOffset startAtUtc,
        int availableMinutes = 300,
        decimal? remainingBudgetVnd = 700_000m) =>
        new(
            startAtUtc,
            new RoutePoint(CenterLatitude, CenterLongitude),
            availableMinutes,
            remainingBudgetVnd,
            5m,
            new RoutePoint(CenterLatitude, CenterLongitude));

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(TestDbContext db) => Db = db;

        public TestDbContext Db { get; }
        public SchedulingRequest Request { get; private set; } = null!;
        public PointOfInterest Museum { get; private set; } = null!;
        public PointOfInterest Bridge { get; private set; } = null!;
        public PointOfInterest Park { get; private set; } = null!;
        public RecordingRouting Routing { get; } = new();
        public ItineraryVersionService Service => new(Db, Routing);

        public static async Task<Fixture> CreateAsync(bool mandatoryPark = false)
        {
            var fixture = new Fixture(TestDbContext.Create());
            fixture.Museum = await fixture.AddPoiAsync("Cham Museum");
            fixture.Bridge = await fixture.AddPoiAsync("Dragon Bridge", 16.0612m);
            fixture.Park = await fixture.AddPoiAsync("Asia Park", 16.0480m);
            fixture.Request = await fixture.AddRequestAsync(mandatoryPark ? $"[{fixture.Park.Id}]" : "[]");
            return fixture;
        }

        public async Task<StopsEvaluation> EvaluateAsync(long[] poiIds, EvaluationAnchor? anchor)
        {
            var result = await Service.EvaluateStopVariantsAsync(
                Request,
                [poiIds.Select(id => new ItineraryStop(id)).ToArray()],
                CancellationToken.None,
                anchor: anchor);
            result.IsSuccess.Should().BeTrue();
            return result.Value[0];
        }

        public async Task<PointOfInterest> AddPoiAsync(string name, decimal latitude = CenterLatitude)
        {
            var poi = PointOfInterest.Create(
                PoiCategory.Create("Culture", null),
                name,
                latitude,
                CenterLongitude,
                1,
                Now,
                averageVisitDurationMinutes: 60);
            poi.ConfigurePlanningMetadata(60_000m, "https://example.com/poi", Now);
            poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
            Db.PointsOfInterest.Add(poi);
            await Db.SaveChangesAsync();
            return poi;
        }

        public async Task<Itinerary> AddItineraryAsync(int version, params long[] visitPoiIds)
        {
            var itinerary = Itinerary.CreateCspGenerated(Request, "Generated trip", RequestStart, Now.AddHours(5), version);
            for (var index = 0; index < visitPoiIds.Length; index++)
            {
                itinerary.AddItem(ItineraryItem.CreateVisit(
                    index + 1, visitPoiIds[index], Now.AddHours(1 + index), Now.AddHours(2 + index),
                    false, 60_000m, "Suggested nearby location"));
            }

            Db.Itineraries.Add(itinerary);
            await Db.SaveChangesAsync();
            return itinerary;
        }

        private async Task<SchedulingRequest> AddRequestAsync(string mandatoryPoiIdsJson)
        {
            var request = SchedulingRequest.Create(
                42,
                Guid.NewGuid(),
                new string('a', SchedulingRequest.RequestHashLength),
                RequestStart,
                "Asia/Ho_Chi_Minh",
                CenterLatitude,
                CenterLongitude,
                CenterLatitude,
                CenterLongitude,
                null,
                true,
                480,
                TransportMode.Motorbike,
                10m,
                800_000m,
                mandatoryPoiIdsJson,
                RestPreference.None,
                Now);
            var owner = Guid.NewGuid();
            request.ClaimGeneration(owner, Now.AddMinutes(1), Now);
            request.CompleteGeneration(owner, Now);
            Db.SchedulingRequests.Add(request);
            await Db.SaveChangesAsync();
            return request;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class RecordingRouting : IRouteDurationProvider
    {
        public List<IReadOnlyList<RoutePoint>> Calls { get; } = [];

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            Calls.Add(points.ToArray());
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
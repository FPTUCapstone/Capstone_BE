using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling;

/// <summary>
/// UC-10 follow-up (2026-11-11): rest stops are real rest places only (food and drink, cafe, restaurant, rest area or
/// stop), at most 10 minutes off the route; otherwise free rest time. Sheltered attractions (beach, natural attraction,
/// park, theme or water park) are places to visit, never rest stops. Local E2E found a 30-minute "rest" at Am Phu Cave
/// 16–24 minutes away, and the same rule hid Ba Na Hills and Am Phu Cave from every itinerary as visits.
/// </summary>
public sealed class RestStopSelectionTests
{
    private const long MuseumId = 12;
    private const long CaveId = 28;
    private const long CafeId = 31;

    private static readonly DateTimeOffset StartAtUtc = new(2026, 11, 10, 1, 0, 0, TimeSpan.Zero); // Tuesday 08:00 +07

    [Theory]
    [InlineData("Food & Drink", true)]
    [InlineData("Cafe", true)]
    [InlineData("Restaurant", true)]
    [InlineData("Rest area", true)]
    [InlineData("Rest stop", true)]
    [InlineData("Natural attraction", false)]
    [InlineData("Beach", false)]
    [InlineData("Park", false)]
    [InlineData("Theme park", false)]
    [InlineData("Water park", false)]
    [InlineData("Museum", false)]
    public void IsRestCategory_AcceptsOnlyRealRestPlaces_WithOrWithoutShelter(string category, bool expected) =>
        ItineraryScheduleEvaluator.IsRestCategory(category).Should().Be(expected);

    /// <summary>Bug A: a sheltered attraction near the route must not become the rest stop.</summary>
    [Theory]
    [InlineData(SchedulingSolverMode.Heuristic)]
    [InlineData(SchedulingSolverMode.Csp)]
    public async Task Generate_NeverRestsAtASheltredAttraction(SchedulingSolverMode mode)
    {
        var plan = await GenerateAsync(mode, [Museum(), Cave(effectiveDesirability: 0m)], NearMatrix());

        plan.Items.Should().NotContain(item => item.Kind == ItineraryItemKind.Rest && item.PointOfInterestId == CaveId);
        plan.Items.Should().Contain(item => item.Kind == ItineraryItemKind.Rest);
    }

    /// <summary>Bug B: the same sheltered attraction is a normal place to visit when the traveler likes it.</summary>
    [Theory]
    [InlineData(SchedulingSolverMode.Heuristic)]
    [InlineData(SchedulingSolverMode.Csp)]
    public async Task Generate_SchedulesASheltredAttractionAsAVisit(SchedulingSolverMode mode)
    {
        var plan = await GenerateAsync(mode, [Museum(), Cave(effectiveDesirability: 1m)], NearMatrix());

        plan.Items.Should().Contain(item => item.Kind == ItineraryItemKind.Visit && item.PointOfInterestId == CaveId);
    }

    /// <summary>A cafe more than 10 minutes off the route is skipped in favor of free rest time.</summary>
    [Fact]
    public async Task Generate_UsesFreeRest_WhenTheOnlyCafeIsMoreThanTenMinutesOffTheRoute()
    {
        // museum → cafe 15 + cafe → end 15 versus museum → end 5: a 25-minute detour.
        var plan = await GenerateAsync(SchedulingSolverMode.Heuristic, [Museum(), Cafe()], Matrix(museumToRest: 15, restToEnd: 15));

        plan.Items.Should().NotContain(item => item.PointOfInterestId == CafeId);
        plan.Items.Should().Contain(item =>
            item.Kind == ItineraryItemKind.Rest && item.PointOfInterestId == null && item.RecommendationReason == "Free/rest time");
    }

    [Fact]
    public async Task Generate_RestsAtTheCafe_WhenItIsAtMostTenMinutesOffTheRoute()
    {
        // museum → cafe 5 + cafe → end 8 versus museum → end 5: an 8-minute detour.
        var plan = await GenerateAsync(SchedulingSolverMode.Heuristic, [Museum(), Cafe()], Matrix(museumToRest: 5, restToEnd: 8));

        plan.Items.Should().Contain(item =>
            item.Kind == ItineraryItemKind.Rest && item.PointOfInterestId == CafeId && item.RecommendationReason == "Suggested rest stop");
    }

    private static async Task<GeneratedItineraryPlan> GenerateAsync(
        SchedulingSolverMode mode,
        GenerationCandidate[] candidates,
        RouteDurationMatrix matrix)
    {
        var options = new SchedulingGenerationOptions { SolverMode = mode, EnableOptionalRouteOptimization = true };
        var input = new GenerationInput(
            StartAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            new RoutePoint(16.0544m, 108.2022m),
            new RoutePoint(16.0600m, 108.2300m),
            300,
            TransportMode.Motorbike,
            RestPreference.Auto,
            500_000m,
            candidates,
            [MuseumId]);

        var result = await new ItineraryGenerationService(new FixedRouteDurationProvider(matrix), options)
            .GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.ErrorMessage : null);
        return result.Value;
    }

    /// <summary>Points: start, museum, rest-or-cave, end. Everything is close: the cave is a 5-minute hop.</summary>
    private static RouteDurationMatrix NearMatrix() => Matrix(museumToRest: 5, restToEnd: 5);

    private static RouteDurationMatrix Matrix(int museumToRest, int restToEnd) =>
        RouteDurationMatrix.Create(new int[,]
        {
            { 0, 10, 10, 10 },
            { 10, 0, museumToRest, 5 },
            { 10, museumToRest, 0, restToEnd },
            { 10, 5, restToEnd, 0 },
        });

    private static GenerationCandidate Museum() =>
        Candidate(MuseumId, "Cham Museum", "Museum", 150, hasShelter: true, effectiveDesirability: 0.5m);

    private static GenerationCandidate Cave(decimal effectiveDesirability) =>
        Candidate(CaveId, "Am Phu Cave", "Natural attraction", 60, hasShelter: true, effectiveDesirability);

    private static GenerationCandidate Cafe() =>
        Candidate(CafeId, "Riverside cafe", "Cafe", 30, hasShelter: true, effectiveDesirability: 0m);

    private static GenerationCandidate Candidate(
        long id,
        string name,
        string category,
        int visitMinutes,
        bool hasShelter,
        decimal effectiveDesirability) =>
        new(
            id,
            name,
            new RoutePoint(16m + (id / 10_000m), 108m + (id / 10_000m)),
            visitMinutes,
            0m,
            [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0))],
            TripMateBaseScore: effectiveDesirability,
            EffectiveDesirabilityScore: effectiveDesirability,
            CategoryName: category,
            HasShelter: hasShelter,
            IndoorOutdoor: IndoorOutdoorType.Mixed);

    private sealed class FixedRouteDurationProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            Task.FromResult(matrix);
    }
}
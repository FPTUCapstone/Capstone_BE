using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public sealed class RestaurantDuplicateDiagnosticTests
{
    [Theory]
    [InlineData(RestPreference.Auto)]
    [InlineData(RestPreference.Frequent)]
    public async Task ExplicitRestaurantVisit_ShouldNotAlsoBecomeAutomaticRest(RestPreference restPreference)
    {
        var museum = Candidate(12, "Museum", "Museum", 150);
        var restaurant = Candidate(31, "Restaurant", "Food & Drink", 45);
        GenerationCandidate[] candidates = [museum, restaurant];
        var input = new GenerationInput(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            new RoutePoint(16m, 108m), new RoutePoint(16m, 108m),
            480, TransportMode.Motorbike, restPreference, null, candidates, [12]);
        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 5, 5, 5 }, { 5, 0, 5, 5 },
            { 5, 5, 0, 5 }, { 5, 5, 5, 0 },
        });
        var result = new ItineraryScheduleEvaluator().Evaluate(input, candidates, matrix,
            candidates, new Dictionary<long, int> { [12] = 1, [31] = 2 });
        result.Should().NotBeNull();
        result!.Plan.Items.Where(item => item.PointOfInterestId == 31)
            .Should().ContainSingle("a manually selected restaurant must appear only once");
        var visit = result.Plan.Items.Single(item => item.PointOfInterestId == 31);
        visit.Kind.Should().Be(ItineraryItemKind.Visit);
        visit.EstimatedCost.Should().Be(150_000m);
        (visit.PlannedDepartureUtc - visit.PlannedArrivalUtc).TotalMinutes.Should().Be(45);
        new GeneratedItineraryInvariantValidator().Validate(input, matrix, candidates, result.Plan);

        var savedPlan = await new ItineraryGenerationService(new FixedRouting(matrix))
            .GenerateFixedOrderAsync(input, [12, 31], CancellationToken.None);
        savedPlan.IsSuccess.Should().BeTrue();
        savedPlan.Value.Items.Where(item => item.PointOfInterestId == 31).Should().ContainSingle();
        savedPlan.Value.Items.Should().NotContain(item => item.Kind == ItineraryItemKind.Rest,
            "the adjacent 45-minute meal satisfies the rest instead of adding another 30-minute pause");
    }

    private static GenerationCandidate Candidate(long id, string name, string category, int duration) =>
        new(id, name, new RoutePoint(16m, 108m), duration, id == 31 ? 150_000m : 0m,
            [new GenerationOpeningHours(2, new TimeOnly(0, 0), new TimeOnly(23, 59))],
            CategoryName: category, HasShelter: true);

    private sealed class FixedRouting(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(IReadOnlyList<RoutePoint> points,
            TransportMode transportMode, CancellationToken cancellationToken) => Task.FromResult(matrix);
    }
}
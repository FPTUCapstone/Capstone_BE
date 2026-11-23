using FluentAssertions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public sealed class DiningRestTests
{
    [Theory]
    [InlineData(5, 45, true, false)]
    [InlineData(6, 45, true, true)]
    [InlineData(5, 29, true, true)]
    [InlineData(5, 45, false, true)]
    public async Task FixedOrder_OnlySubstitutesANearbyLongShelteredMeal(
        int legMinutes, int mealMinutes, bool sheltered, bool expectAutomaticRest)
    {
        var (input, matrix) = Scenario(legMinutes, mealMinutes, sheltered);
        var result = await Generate(input, matrix, [12, 31]);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Any(item => item.Kind == ItineraryItemKind.Rest).Should().Be(expectAutomaticRest);
        result.Value.Items.Count(item => item.PointOfInterestId == 31).Should().Be(1);
        result.Value.TotalEstimatedCost.Should().Be(210_000m);
    }

    [Theory]
    [InlineData(RestPreference.Auto, 0)]
    [InlineData(RestPreference.Frequent, 1)]
    [InlineData(RestPreference.None, 0)]
    public async Task Meal_ResetsContinuousActivity_AndSatisfiesAutoRest(RestPreference preference, int expectedRests)
    {
        var (input, matrix) = Scenario(5, 45, true);
        input = input with
        {
            RestPreference = preference,
            AvailableMinutes = 600,
            Candidates = input.Candidates.Append(Poi(44, "Museum", 150, true)).ToArray()
        };
        matrix = RouteDurationMatrix.Create(new int[,] {
            {0,5,5,5,5}, {5,0,5,5,5}, {5,5,0,5,5}, {5,5,5,0,5}, {5,5,5,5,0} });
        var result = await Generate(input, matrix, [12, 31, 44]);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Count(item => item.Kind == ItineraryItemKind.Rest).Should().Be(expectedRests);
        result.Value.Items.Single(item => item.PointOfInterestId == 44).PlannedArrivalUtc
            .Should().Be(result.Value.Items.Single(item => item.PointOfInterestId == 31).PlannedDepartureUtc.AddMinutes(15));
    }

    [Fact]
    public async Task Meal_OpeningWaitBeyondDeferralLimit_DoesNotRemoveRest()
    {
        var (input, matrix) = Scenario(5, 45, true);
        input = input with
        {
            Candidates = input.Candidates.Select(candidate => candidate.Id == 31
            ? candidate with { OpeningHours = [new GenerationOpeningHours(2, new TimeOnly(11, 30), new TimeOnly(23, 59))] }
            : candidate).ToArray()
        };
        var result = await Generate(input, matrix, [12, 31]);
        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().Contain(item => item.Kind == ItineraryItemKind.Rest);
    }

    [Fact]
    public async Task Meal_DoesNotBypassBudgetOrOpeningHours()
    {
        var (input, matrix) = Scenario(5, 45, true);
        (await Generate(input with { BudgetVnd = 200_000m }, matrix, [12, 31])).IsFailure.Should().BeTrue();
        input = input with
        {
            Candidates = input.Candidates.Select(candidate => candidate.Id == 31
            ? candidate with { OpeningHours = [] } : candidate).ToArray()
        };
        (await Generate(input, matrix, [12, 31])).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(10, 30, 30, false, IndoorOutdoorType.Outdoor, true)]
    [InlineData(10, 31, 30, false, IndoorOutdoorType.Outdoor, false)]
    [InlineData(14, 29, 30, false, IndoorOutdoorType.Outdoor, false)]
    [InlineData(14, 30, 30, false, IndoorOutdoorType.Outdoor, true)]
    [InlineData(12, 0, 45, false, IndoorOutdoorType.Indoor, true)]
    [InlineData(12, 0, 45, true, IndoorOutdoorType.Outdoor, true)]
    public void DiningRest_RequiresShelterForAnyOverlapWithMidday(int hour, int minute, int duration,
        bool sheltered, IndoorOutdoorType environment, bool qualifies)
    {
        var poi = Poi(31, "Food & Drink", duration, sheltered) with { IndoorOutdoor = environment };
        ItineraryScheduleEvaluator.IsDiningRest(poi,
            new DateTimeOffset(2026, 11, 10, hour, minute, 0, TimeSpan.FromHours(7)),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")).Should().Be(qualifies);
    }

    private static (GenerationInput, RouteDurationMatrix) Scenario(int legMinutes, int mealMinutes, bool sheltered)
    {
        var input = new GenerationInput(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"), new RoutePoint(16m, 108m), new RoutePoint(16m, 108m),
            480, TransportMode.Motorbike, RestPreference.Auto, 500_000m,
            [Poi(12, "Museum", 150, true), Poi(31, "Food & Drink", mealMinutes, sheltered)], [12]);
        return (input, RouteDurationMatrix.Create(new int[,] {
            {0,5,5,5},{5,0,legMinutes,5},{5,legMinutes,0,5},{5,5,5,0} }));
    }

    private static GenerationCandidate Poi(long id, string category, int duration, bool sheltered) =>
        new(id, category, new RoutePoint(16m, 108m), duration, id == 31 ? 150_000m : 60_000m,
            [new GenerationOpeningHours(2, new TimeOnly(0, 0), new TimeOnly(23, 59))], CategoryName: category, HasShelter: sheltered);

    private static Task<TripMate.Application.Common.Models.Result<GeneratedItineraryPlan>> Generate(
        GenerationInput input, RouteDurationMatrix matrix, long[] order) =>
        new ItineraryGenerationService(new FixedRouting(matrix)).GenerateFixedOrderAsync(input, order, CancellationToken.None);

    private sealed class FixedRouting(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(IReadOnlyList<RoutePoint> points, TransportMode mode,
            CancellationToken token) => Task.FromResult(matrix);
    }
}
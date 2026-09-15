using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling;

public class ItineraryGenerationServiceTests
{
    private static readonly DateTimeOffset StartAtUtc =
        new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Generate_IncludesAllMandatoryPois_AndReachesEndInTime()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 20, 35, 15 },
                { 20, 0, 15, 25 },
                { 35, 15, 0, 20 },
                { 15, 25, 20, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Cham Museum", 60, 60_000m),
                Candidate(28, "Fine Arts Museum", 60, 20_000m),
            ],
            mandatoryPoiIds: [12, 28]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.IsMandatory)
            .Select(item => item.PointOfInterestId)
            .Should().BeEquivalentTo([12L, 28L]);
        result.Value.TotalDurationMinutes.Should().BeLessThanOrEqualTo(240);
        result.Value.EndAtUtc.Should().BeOnOrBefore(StartAtUtc.AddMinutes(240));
    }

    [Fact]
    public async Task Generate_AutoRest_InsertsRestAfterLongContinuousSchedule()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 20, 35, 15 },
                { 20, 0, 15, 25 },
                { 35, 15, 0, 20 },
                { 15, 25, 20, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 480,
            restPreference: RestPreference.Auto,
            candidates:
            [
                Candidate(12, "Cham Museum", 150, 60_000m),
                Candidate(28, "Fine Arts Museum", 150, 20_000m),
            ],
            mandatoryPoiIds: [12, 28]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().Contain(item =>
            item.Kind == ItineraryItemKind.Rest
            && !item.PointOfInterestId.HasValue
            && item.RecommendationReason == "Free/rest time");
    }

    [Fact]
    public async Task Generate_WhenOnlyUnbufferedScheduleFits_ReturnsInfeasible()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 20, 20 },
                { 20, 0, 20 },
                { 20, 20, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 120,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Cham Museum", 60, 60_000m),
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_WhenNoVisitCanFit_ReturnsInfeasibleInsteadOfAnEmptyItinerary()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 20, 20 },
                { 20, 0, 20 },
                { 20, 20, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 60,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Cham Museum", 60, 60_000m),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_WhenTimeAndBudgetRemain_AddsFeasibleOptionalPoi()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 20, 15, 15 },
                { 20, 0, 10, 25 },
                { 15, 10, 0, 15 },
                { 15, 25, 15, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Cham Museum", 60, 60_000m),
                Candidate(28, "Fine Arts Museum", 45, 20_000m),
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().Contain(item =>
            item.PointOfInterestId == 28
            && !item.IsMandatory
            && item.RecommendationReason == "Suggested nearby location");
    }

    [Fact]
    public async Task Generate_FrequentRest_InsertsBreaksAfterTwoHoursOfContinuousSchedule()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 20, 35, 15 },
                { 20, 0, 15, 25 },
                { 35, 15, 0, 20 },
                { 15, 25, 20, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 480,
            restPreference: RestPreference.Frequent,
            candidates:
            [
                Candidate(12, "Cham Museum", 120, 60_000m),
                Candidate(28, "Fine Arts Museum", 120, 20_000m),
            ],
            mandatoryPoiIds: [12, 28]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Count(item => item.Kind == ItineraryItemKind.Rest).Should().BeGreaterThanOrEqualTo(2);
    }

    private static GenerationInput CreateInput(
        int availableMinutes,
        RestPreference restPreference,
        IReadOnlyCollection<GenerationCandidate> candidates,
        IReadOnlyCollection<long> mandatoryPoiIds) =>
        new(
            StartAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            new RoutePoint(16.0544m, 108.2022m),
            new RoutePoint(16.0600m, 108.2300m),
            availableMinutes,
            TransportMode.Motorbike,
            restPreference,
            BudgetVnd: 200_000m,
            candidates,
            mandatoryPoiIds);

    private static GenerationCandidate Candidate(
        long id,
        string name,
        int visitDurationMinutes,
        decimal cost) =>
        new(
            id,
            name,
            new RoutePoint(16m + (id / 10_000m), 108m + (id / 10_000m)),
            visitDurationMinutes,
            cost,
            [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0))]);

    private sealed class FixedRouteDurationProvider(RouteDurationMatrix matrix)
        : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            Task.FromResult(matrix);
    }
}

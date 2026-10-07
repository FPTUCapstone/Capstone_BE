using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
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
                Candidate(12, "Cham Museum", 60, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Fine Arts Museum", 60, 20_000m, 0m, 0m, null, null, null),
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
    public async Task Generate_PreservesTravelDurationBetweenItineraryItems()
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
                Candidate(12, "Cham Museum", 60, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Fine Arts Museum", 60, 20_000m, 0m, 0m, null, null, null),
            ],
            mandatoryPoiIds: [12, 28]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var items = result.Value.Items.ToArray();
        items[^1].TravelDurationToNextMinutes.Should().BeNull();
        items[0].TravelDurationToNextMinutes.Should().BeOneOf(15, 25);
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
                Candidate(12, "Cham Museum", 150, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Fine Arts Museum", 150, 20_000m, 0m, 0m, null, null, null),
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
                Candidate(12, "Cham Museum", 60, 60_000m, 0m, 0m, null, null, null),
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
                Candidate(12, "Cham Museum", 60, 60_000m, 0m, 0m, null, null, null),
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
                Candidate(12, "Cham Museum", 60, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Fine Arts Museum", 45, 20_000m, 0m, 0m, null, null, null),
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
    public async Task Generate_AddsMultipleOptionalPoisInDeterministicRouteOrder()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Lower-ranked museum", 30, 20_000m, 0.3m, 0.4m, 2m, 3m, 20_000m),
                Candidate(28, "Higher-ranked museum", 30, 40_000m, 0.6m, 0.8m, 5m, 4m, 40_000m),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId)
            .Should().Equal(12L, 28L);
    }

    [Fact]
    public async Task Generate_AutoRestPrefersAQualifiedRestPoi()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 20, 10, 15 },
                { 20, 0, 10, 20 },
                { 10, 10, 0, 10 },
                { 15, 20, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 300,
            restPreference: RestPreference.Auto,
            candidates:
            [
                Candidate(12, "Long museum visit", 150, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Riverside cafe", 30, 20_000m, 0m, 0m, null, null, null) with
                {
                    CategoryName = "Cafe",
                    HasShelter = true,
                },
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().Contain(item =>
            item.Kind == ItineraryItemKind.Rest
            && item.PointOfInterestId == 28
            && item.PointOfInterestName == "Riverside cafe");
    }

    [Fact]
    public async Task Generate_AutoRestTracksOptionalStopsAndInsertsBreak()
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
                Candidate(12, "Long optional museum", 150, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Second optional museum", 120, 20_000m, 0m, 0m, null, null, null),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().Contain(item => item.Kind == ItineraryItemKind.Rest);
    }

    [Fact]
    public async Task Generate_AutoRestAcceptsShelteredOutdoorRestCategory()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 20, 10, 15 },
                { 20, 0, 10, 20 },
                { 10, 10, 0, 10 },
                { 15, 20, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 300,
            restPreference: RestPreference.Auto,
            candidates:
            [
                Candidate(12, "Long museum visit", 150, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Sheltered riverside stop", 30, 0m, 0m, 0m, null, null, null) with
                {
                    CategoryName = "Natural attraction",
                    HasShelter = true,
                },
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().Contain(item =>
            item.Kind == ItineraryItemKind.Rest
            && item.PointOfInterestId == 28);
    }

    [Fact]
    public async Task GenerateFixedOrder_DoesNotAllowRemovingMandatoryPoi()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Mandatory museum", 30, 20_000m),
                Candidate(28, "Optional museum", 30, 20_000m),
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateFixedOrderAsync(
            input,
            orderedVisitPoiIds: [28],
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("Mandatory locations");
    }

    [Fact]
    public async Task GenerateFixedOrder_ClassifiesEachStopByCanonicalMandatoryRequest()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Mandatory museum", 30, 20_000m),
                Candidate(28, "Optional museum", 30, 20_000m),
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateFixedOrderAsync(
            input,
            orderedVisitPoiIds: [12, 28],
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var visitItems = result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .ToArray();
        var mandatoryItem = visitItems.Single(item => item.PointOfInterestId == 12);
        mandatoryItem.IsMandatory.Should().BeTrue();
        mandatoryItem.RecommendationReason.Should().Be("Mandatory location");
        var optionalItem = visitItems.Single(item => item.PointOfInterestId == 28);
        optionalItem.IsMandatory.Should().BeFalse();
        optionalItem.RecommendationReason.Should().NotBe("Mandatory location");
        optionalItem.RecommendationReason.Should().Be("Suggested nearby location");
    }

    [Fact]
    public async Task Generate_ZeroMandatoryPois_NoStopIsLabeledMandatory()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Cham Museum", 30, 20_000m),
                Candidate(28, "Fine Arts Museum", 30, 20_000m),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var visitItems = result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .ToArray();
        visitItems.Should().NotBeEmpty();
        visitItems.Should().OnlyContain(item =>
            !item.IsMandatory
            && item.RecommendationReason != "Mandatory location");
    }

    [Fact]
    public async Task Generate_MixedMandatoryAndOptional_MatchesReasonToEachItemMetadata()
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
                Candidate(12, "Cham Museum", 60, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Fine Arts Museum", 45, 20_000m, 0m, 0m, null, null, null),
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var mandatoryItem = result.Value.Items.Single(item => item.PointOfInterestId == 12);
        mandatoryItem.IsMandatory.Should().BeTrue();
        mandatoryItem.RecommendationReason.Should().Be("Mandatory location");
        var optionalItem = result.Value.Items.Single(item => item.PointOfInterestId == 28);
        optionalItem.IsMandatory.Should().BeFalse();
        optionalItem.RecommendationReason.Should().Be("Suggested nearby location");
    }

    [Fact]
    public async Task Generate_QualifiedRestPoi_KeepsRestWordingAndOptionalFlag()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 20, 10, 15 },
                { 20, 0, 10, 20 },
                { 10, 10, 0, 10 },
                { 15, 20, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 300,
            restPreference: RestPreference.Auto,
            candidates:
            [
                Candidate(12, "Long museum visit", 150, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Riverside cafe", 30, 20_000m, 0m, 0m, null, null, null) with
                {
                    CategoryName = "Cafe",
                    HasShelter = true,
                },
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().Contain(item =>
            item.Kind == ItineraryItemKind.Rest
            && item.PointOfInterestId == 28
            && !item.IsMandatory
            && item.RecommendationReason == "Suggested rest stop");
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
                Candidate(12, "Cham Museum", 120, 60_000m, 0m, 0m, null, null, null),
                Candidate(28, "Fine Arts Museum", 120, 20_000m, 0m, 0m, null, null, null),
            ],
            mandatoryPoiIds: [12, 28]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Count(item => item.Kind == ItineraryItemKind.Rest).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Generate_OrdersOptionalsByEffectiveBeforeLegacyPreferenceScore()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Higher effective", 30, 20_000m, 0m, 0.9m, null, null, null) with
                {
                    PreferenceScore = 0,
                },
                Candidate(28, "Higher legacy preference", 30, 20_000m, 0m, 0.1m, null, null, null) with
                {
                    PreferenceScore = 100,
                },
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId)
            .Should().Equal(12L, 28L);
    }

    [Theory]
    [InlineData("scenic", 28L)]
    [InlineData("scenic-null", 28L)]
    [InlineData("photo", 28L)]
    [InlineData("photo-null", 28L)]
    [InlineData("matrix-minutes", 28L)]
    [InlineData("ranking-cost", 28L)]
    [InlineData("ranking-cost-null", 28L)]
    [InlineData("poi-id", 12L)]
    public async Task Generate_UsesCanonicalFrozenTieBreakOrder(string scenario, long expectedFirstId)
    {
        var first = Candidate(12, "First candidate", 30, 20_000m, 0m, 0.5m, 5m, 5m, 40_000m);
        var second = Candidate(28, "Second candidate", 30, 40_000m, 0m, 0.5m, 5m, 5m, 40_000m);
        var firstMinutes = 10;
        var secondMinutes = 10;

        switch (scenario)
        {
            case "scenic":
                first = first with { ScenicScoreForRanking = 8m, ScenicScore = 10m };
                second = second with { ScenicScoreForRanking = 9m, ScenicScore = 1m };
                break;
            case "scenic-null":
                first = first with { ScenicScoreForRanking = null, ScenicScore = 10m };
                second = second with { ScenicScoreForRanking = 5m, ScenicScore = 1m };
                break;
            case "photo":
                first = first with { PhotoRatingForRanking = 8m, PhotoRating = 10m };
                second = second with { PhotoRatingForRanking = 9m, PhotoRating = 1m };
                break;
            case "photo-null":
                first = first with { PhotoRatingForRanking = null, PhotoRating = 10m };
                second = second with { PhotoRatingForRanking = 5m, PhotoRating = 1m };
                break;
            case "matrix-minutes":
                firstMinutes = 30;
                secondMinutes = 10;
                break;
            case "ranking-cost":
                first = first with { EstimatedVisitCostForRanking = 50_000m };
                second = second with { EstimatedVisitCostForRanking = 30_000m };
                break;
            case "ranking-cost-null":
                first = first with { EstimatedVisitCostForRanking = null };
                second = second with { EstimatedVisitCostForRanking = 30_000m };
                break;
            case "poi-id":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, firstMinutes, secondMinutes, 10 },
                { firstMinutes, 0, 10, 10 },
                { secondMinutes, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 90,
            restPreference: RestPreference.None,
            candidates: [first, second],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId)
            .Should().Equal(expectedFirstId);
    }

    [Fact]
    public async Task Generate_HigherRankedCandidateStillYieldsToCurrentBudgetFeasibility()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Higher-ranked but over budget", 30, 150_000m, 0m, 0.9m, 9m, 9m, 1m),
                Candidate(28, "Lower-ranked and feasible", 30, 40_000m, 0m, 0.1m, 1m, 1m, 200_000m),
            ],
            mandatoryPoiIds: [],
            budgetVnd: 100_000m);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Should().ContainSingle(item =>
                item.PointOfInterestId == 28
                && item.EstimatedCost == 40_000m);
        result.Value.TotalEstimatedCost.Should().Be(40_000m);
    }

    [Fact]
    public async Task Generate_TopRankedOptionalClosedBeforeArrival_IsExcluded()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(
                    12,
                    "Top-ranked but closed",
                    30,
                    40_000m,
                    0m,
                    0.9m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(8, 0))]),
                Candidate(
                    28,
                    "Lower-ranked and open",
                    30,
                    40_000m,
                    0m,
                    0.1m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0))]),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId)
            .Should().Equal(28L);
    }

    [Fact]
    public async Task Generate_OptionalArrivingBeforeOpening_IsAlignedToOpeningTime()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10 },
                { 10, 0, 10 },
                { 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(
                    12,
                    "Museum opening later",
                    60,
                    40_000m,
                    0m,
                    1.0m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(10, 0), new TimeOnly(18, 0))]),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var visits = result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .ToArray();
        visits.Should().ContainSingle(item => item.PointOfInterestId == 12);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        TimeZoneInfo.ConvertTime(visits[0].PlannedArrivalUtc, timeZone).TimeOfDay
            .Should().Be(new TimeSpan(10, 0, 0));
        TimeZoneInfo.ConvertTime(visits[0].PlannedDepartureUtc, timeZone).TimeOfDay
            .Should().Be(new TimeSpan(11, 0, 0));
    }

    [Fact]
    public async Task Generate_OptionalVisitOverrunningClose_IsExcluded()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(
                    12,
                    "Visit overruns close",
                    120,
                    40_000m,
                    0m,
                    0.9m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(9, 30))]),
                Candidate(
                    28,
                    "Feasible alternative",
                    30,
                    40_000m,
                    0m,
                    0.1m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0))]),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId)
            .Should().Equal(28L);
    }

    [Fact]
    public async Task Generate_OptionalWithoutHoursForArrivalDay_IsExcluded()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                // Closed days are absent here because ToCandidate filters closed rows.
                Candidate(
                    12,
                    "No Tuesday hours",
                    30,
                    40_000m,
                    0m,
                    0.9m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(1, new TimeOnly(7, 0), new TimeOnly(20, 0))]),
                Candidate(
                    28,
                    "Open Tuesday",
                    30,
                    40_000m,
                    0m,
                    0.1m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0))]),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId)
            .Should().Equal(28L);
    }

    [Fact]
    public async Task Generate_MandatoryWithImpossibleHours_ReturnsConstraintsInfeasible()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10 },
                { 10, 0, 10 },
                { 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(
                    12,
                    "Mandatory but closed",
                    60,
                    40_000m,
                    0m,
                    0m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(8, 0))]),
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
    }

    [Fact]
    public async Task Generate_MaxEffectiveScoreCandidate_ViolatingHours_StillExcluded()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(
                    12,
                    "Maximum score but closed",
                    30,
                    40_000m,
                    0m,
                    1.0m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(8, 0))]),
                Candidate(
                    28,
                    "Zero score and feasible",
                    30,
                    40_000m,
                    0m,
                    0.0m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0))]),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId)
            .Should().Equal(28L);
    }

    [Fact]
    public async Task Generate_MaxEffectiveScoreCandidate_ViolatingTimeWindow_StillExcluded()
    {
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(
            RouteDurationMatrix.Create(
            new int[,]
            {
                { 0, 10, 10, 10 },
                { 10, 0, 10, 10 },
                { 10, 10, 0, 10 },
                { 10, 10, 10, 0 },
            })));
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(
                    12,
                    "Maximum score but exceeds time window",
                    300,
                    40_000m,
                    0m,
                    1.0m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(0, 0), new TimeOnly(23, 59))]),
                Candidate(
                    28,
                    "Zero score and within time window",
                    30,
                    40_000m,
                    0m,
                    0.0m,
                    null,
                    null,
                    null,
                    [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0))]),
            ],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId)
            .Should().Equal(28L);
        result.Value.TotalDurationMinutes.Should().BeLessThanOrEqualTo(240);
    }

    [Fact]
    public async Task GenerateAsync_WithZeroBudget_ReturnsInvalidRequestResultWithoutCallingProvider()
    {
        var service = new ItineraryGenerationService(new ThrowingRouteDurationProvider());
        var input = CreateInput(480, RestPreference.None,
            [Candidate(12, "Museum", 60, 10_000m)], [12], budgetVnd: 0m);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Be("The itinerary request is invalid.");
    }

    [Fact]
    public async Task GenerateFixedOrderAsync_WithZeroBudget_ReturnsInvalidRequestResultWithoutCallingProvider()
    {
        var service = new ItineraryGenerationService(new ThrowingRouteDurationProvider());
        var input = CreateInput(480, RestPreference.None,
            [Candidate(12, "Museum", 60, 10_000m)], [12], budgetVnd: 0m);

        var result = await service.GenerateFixedOrderAsync(input, [12], CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Be("The itinerary request is invalid.");
    }

    [Fact]
    public async Task GenerateAsync_WithDuplicateCandidateIds_ReturnsInfeasibleWithoutThrowingException()
    {
        var service = new ItineraryGenerationService(new ThrowingRouteDurationProvider());
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Cham Museum", 60, 60_000m),
                Candidate(12, "Cham Museum Duplicate", 60, 60_000m),
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        result.ErrorMessage.Should().Be("A mandatory location is unavailable.");
    }

    [Fact]
    public async Task GenerateFixedOrderAsync_WithDuplicateCandidateIds_ReturnsInfeasibleWithoutThrowingException()
    {
        var service = new ItineraryGenerationService(new ThrowingRouteDurationProvider());
        var input = CreateInput(
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates:
            [
                Candidate(12, "Cham Museum", 60, 60_000m),
                Candidate(12, "Cham Museum Duplicate", 60, 60_000m),
            ],
            mandatoryPoiIds: [12]);

        var result = await service.GenerateFixedOrderAsync(input, [12], CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        result.ErrorMessage.Should().Be("A selected visit location is unavailable.");
    }

    [Fact]
    public async Task GenerateAsync_Characterize_TailOnlyZigzagBehavior()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(matrix));

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // In legacy append-only, both mandatory POIs are visited, and whatever fits at the tail is appended.
        result.Value.Items.Where(it => it.Kind == ItineraryItemKind.Visit)
            .Select(it => it.PointOfInterestId)
            .Should().Contain([101L, 102L]);
    }

    [Fact]
    public async Task GenerateAsync_Characterize_TailInfeasibleCandidateIsOmittedInLegacy()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateTailInfeasibleMiddleFeasibleScenario();
        var service = new ItineraryGenerationService(
            new FixedRouteDurationProvider(matrix),
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = false });

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Candidate 201 is feasible if inserted between Start and 101, but in legacy tail-append it is omitted.
        result.Value.Items.Should().NotContain(item => item.PointOfInterestId == 201L);
    }

    [Fact]
    public async Task GenerateAsync_Characterize_CanonicalRankingTieBreakOrderIsPreserved()
    {
        // 4 optional candidates with different tie-break attributes
        var cand1 = OptionalRouteOptimizationScenarios.CreateCandidate(201, "Opt1", 30, 10_000m, 0.9m, scenicScoreForRanking: 5m, photoRatingForRanking: 4m);
        var cand2 = OptionalRouteOptimizationScenarios.CreateCandidate(202, "Opt2", 30, 10_000m, 0.9m, scenicScoreForRanking: 4m, photoRatingForRanking: 5m);
        var cand3 = OptionalRouteOptimizationScenarios.CreateCandidate(203, "Opt3", 30, 10_000m, 0.8m);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0, 10, 10, 10, 10 },
            { 10, 0, 10, 10, 10 },
            { 10, 10, 0, 10, 10 },
            { 10, 10, 10, 0, 10 },
            { 10, 10, 10, 10, 0 },
        });

        var input = OptionalRouteOptimizationScenarios.CreateInput(240, [cand1, cand2, cand3], []);
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(matrix));

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Cand1 should be admitted before Cand2 because scenicScore 5 > 4
        var visitIds = result.Value.Items.Where(i => i.Kind == ItineraryItemKind.Visit).Select(i => i.PointOfInterestId).ToList();
        visitIds.Should().Contain([201L, 202L]);
        visitIds.IndexOf(201L).Should().BeLessThan(visitIds.IndexOf(202L));
    }

    [Fact]
    public async Task GenerateAsync_Characterize_AsymmetricMatrixPreservesDirectionality()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateAsymmetricScenario();
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(matrix));

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().Contain(it => it.PointOfInterestId == 101L);
    }

    [Fact]
    public async Task GenerateAsync_CallsRouteProviderExactlyOnce()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var countingProvider = new CountingRouteDurationProvider(matrix);
        var service = new ItineraryGenerationService(countingProvider);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        countingProvider.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateAsync_ZigzagScenario_ReducesTravelTimeSubstantially()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();

        var disabledService = new ItineraryGenerationService(
            new FixedRouteDurationProvider(matrix),
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = false });

        var enabledService = new ItineraryGenerationService(
            new FixedRouteDurationProvider(matrix),
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = true });

        var disabledResult = await disabledService.GenerateAsync(input, CancellationToken.None);
        var enabledResult = await enabledService.GenerateAsync(input, CancellationToken.None);

        disabledResult.IsSuccess.Should().BeTrue();
        enabledResult.IsSuccess.Should().BeTrue();

        // Enabled route should achieve strictly less total duration than disabled zigzag
        enabledResult.Value.TotalDurationMinutes.Should().BeLessThan(disabledResult.Value.TotalDurationMinutes);

        // Equal-objective route orders use the lexicographically lowest visit-ID sequence.
        var visitIds = enabledResult.Value.Items
            .Where(it => it.Kind == ItineraryItemKind.Visit)
            .Select(it => it.PointOfInterestId)
            .ToList();
        visitIds.Should().Equal(101L, 201L, 102L, 202L);
    }

    [Fact]
    public async Task GenerateAsync_TailInfeasibleMiddleFeasible_AdmitsCandidateThroughInsertion()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateTailInfeasibleMiddleFeasibleScenario();

        var disabledService = new ItineraryGenerationService(
            new FixedRouteDurationProvider(matrix),
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = false });

        var enabledService = new ItineraryGenerationService(
            new FixedRouteDurationProvider(matrix),
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = true });

        var disabledResult = await disabledService.GenerateAsync(input, CancellationToken.None);
        var enabledResult = await enabledService.GenerateAsync(input, CancellationToken.None);

        disabledResult.IsSuccess.Should().BeTrue();
        enabledResult.IsSuccess.Should().BeTrue();

        // In disabled mode, optional 201 cannot fit at tail (only 101 is visited)
        disabledResult.Value.Items
            .Where(it => it.Kind == ItineraryItemKind.Visit)
            .Select(it => it.PointOfInterestId)
            .Should().Equal(101L);

        // In enabled mode, optional 201 is inserted before 101 and both fit
        enabledResult.Value.Items
            .Where(it => it.Kind == ItineraryItemKind.Visit)
            .Select(it => it.PointOfInterestId)
            .Should().Equal(201L, 101L);
    }

    [Fact]
    public async Task GenerateAsync_MovesNeverAlterMandatoryMetadata()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var service = new ItineraryGenerationService(
            new FixedRouteDurationProvider(matrix),
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = true });

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var visits = result.Value.Items.Where(it => it.Kind == ItineraryItemKind.Visit).ToList();

        foreach (var visit in visits)
        {
            var isMandatory = input.MandatoryPoiIds.Contains(visit.PointOfInterestId!.Value);
            visit.IsMandatory.Should().Be(isMandatory);
            visit.RecommendationReason.Should().Be(isMandatory ? "Mandatory location" : "Suggested nearby location");
        }
    }

    [Fact]
    public async Task GenerateAsync_RepeatedRuns_AreStrictlyDeterministic()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 2, seed: 123);
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(matrix));

        var run1 = await service.GenerateAsync(input, CancellationToken.None);
        var run2 = await service.GenerateAsync(input, CancellationToken.None);

        run1.IsSuccess.Should().BeTrue();
        run2.IsSuccess.Should().BeTrue();

        run1.Value.TotalDurationMinutes.Should().Be(run2.Value.TotalDurationMinutes);
        run1.Value.TotalEstimatedCost.Should().Be(run2.Value.TotalEstimatedCost);
        run1.Value.EndAtUtc.Should().Be(run2.Value.EndAtUtc);
        run1.Value.Items.Count.Should().Be(run2.Value.Items.Count);

        for (var i = 0; i < run1.Value.Items.Count; i++)
        {
            var item1 = run1.Value.Items.ElementAt(i);
            var item2 = run2.Value.Items.ElementAt(i);

            item1.SequenceNo.Should().Be(item2.SequenceNo);
            item1.PointOfInterestId.Should().Be(item2.PointOfInterestId);
            item1.Kind.Should().Be(item2.Kind);
            item1.PlannedArrivalUtc.Should().Be(item2.PlannedArrivalUtc);
            item1.PlannedDepartureUtc.Should().Be(item2.PlannedDepartureUtc);
            item1.TravelDurationToNextMinutes.Should().Be(item2.TravelDurationToNextMinutes);
        }
    }

    [Fact]
    public async Task GenerateAsync_Cancellation_ExitsPromptly()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 2);
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(matrix));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await service.GenerateAsync(input, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GenerateAsync_AttractiveButInfeasibleMove_IsRejected()
    {
        // POI 101 must be visited in morning (08:00 - 10:00).
        // POI 102 must be visited in afternoon (14:00 - 16:00).
        // Even if visiting 102 before 101 would have shorter travel, opening hours forbid it.
        var opening101 = new GenerationOpeningHours[] { new(2, new TimeOnly(8, 0), new TimeOnly(10, 0)) };
        var opening102 = new GenerationOpeningHours[] { new(2, new TimeOnly(14, 0), new TimeOnly(16, 0)) };

        var cand1 = OptionalRouteOptimizationScenarios.CreateCandidate(101, "MorningMuseum", 30, 10_000m, 0.5m, openingHours: opening101);
        var cand2 = OptionalRouteOptimizationScenarios.CreateCandidate(102, "AfternoonPark", 30, 10_000m, 0.5m, openingHours: opening102);

        var matrix = RouteDurationMatrix.Create(new int[,]
        {
            { 0,  50, 10, 60 },
            { 50,  0, 10, 20 },
            { 10, 10,  0, 50 },
            { 60, 20, 50,  0 },
        });

        var input = OptionalRouteOptimizationScenarios.CreateInput(600, [cand1, cand2], [101, 102]);
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(matrix));

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // 101 must remain before 102 because of opening hours despite matrix distance
        var visitIds = result.Value.Items.Where(it => it.Kind == ItineraryItemKind.Visit).Select(it => it.PointOfInterestId).ToList();
        visitIds.Should().Equal(101L, 102L);
    }

    [Fact]
    public void OptionalRouteOptimizer_TracksEvaluationAndMoveCountersAccurately()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var evaluator = new ItineraryScheduleEvaluator();
        var options = new SchedulingGenerationOptions
        {
            EnableOptionalRouteOptimization = true,
            MaxRouteEvaluations = 500,
        };
        var optimizer = new OptionalRouteOptimizer(evaluator, options);

        var mandatoryCandidates = input.MandatoryPoiIds
            .Select(id => input.Candidates.First(c => c.Id == id))
            .ToArray();
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = new Dictionary<long, int>();
        for (var i = 0; i < matrixCandidates.Length; i++)
        {
            candidateMatrixIndices[matrixCandidates[i].Id] = i + 1;
        }

        var result = optimizer.Optimize(
            input,
            mandatoryCandidates,
            matrix,
            matrixCandidates,
            candidateMatrixIndices,
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.EvaluationsCount.Should().BeGreaterThan(0);
        result.EvaluationsCount.Should().BeLessThanOrEqualTo(500);
        result.BudgetExhausted.Should().BeFalse();
        result.BaselineSchedule.Should().NotBeNull();
        result.Schedule.TotalMatrixTravelMinutes.Should().BeLessThan(result.BaselineSchedule.TotalMatrixTravelMinutes);
    }

    [Fact]
    public async Task GenerateAsync_RecordsActivitySourceDiagnostics()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateZigzagScenario();
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(matrix));

        System.Diagnostics.Activity? recordedActivity = null;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == RouteOptimizationDiagnostics.ActivitySourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act => recordedActivity = act,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        recordedActivity.Should().NotBeNull();
        recordedActivity!.GetTagItem("optimization.enabled").Should().Be(true);
        recordedActivity.GetTagItem("optimization.baseline_optional_count").Should().NotBeNull();
        recordedActivity.GetTagItem("optimization.final_optional_count").Should().NotBeNull();
        recordedActivity.GetTagItem("optimization.evaluations_count").Should().NotBeNull();
        recordedActivity.GetTagItem("optimization.baseline_travel_minutes").Should().NotBeNull();
        recordedActivity.GetTagItem("optimization.final_travel_minutes").Should().NotBeNull();
        recordedActivity.GetTagItem("optimization.travel_delta_minutes").Should().NotBeNull();
    }

    [Fact]
    public async Task GenerateAsync_TwoOptOperator_StrictlyImprovesTravelWithoutChangingVisitSet()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateTwoOptStrictImprovementScenario();
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(matrix));

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // 2-opt uncrosses the segment [102, 103] into [103, 102]
        var visitIds = result.Value.Items
            .Where(it => it.Kind == ItineraryItemKind.Visit)
            .Select(it => it.PointOfInterestId)
            .ToList();
        visitIds.Should().Equal(101L, 103L, 102L, 104L);

        // Verify optimizer metrics directly
        var evaluator = new ItineraryScheduleEvaluator();
        var optimizer = new OptionalRouteOptimizer(evaluator);
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        var mandatoryCandidates = input.MandatoryPoiIds.Select(id => matrixCandidates.First(c => c.Id == id)).ToArray();
        var optResult = optimizer.Optimize(input, mandatoryCandidates, matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);

        optResult.Should().NotBeNull();
        optResult!.TwoOptMovesCount.Should().BeGreaterThan(0);
        optResult.Schedule.TotalMatrixTravelMinutes.Should().BeLessThan(optResult.BaselineSchedule.TotalMatrixTravelMinutes);
        optResult.Schedule.VisitPoiIds.Should().BeEquivalentTo(optResult.BaselineSchedule.VisitPoiIds);
    }

    [Fact]
    public async Task GenerateAsync_RelocateOperator_StrictlyImprovesTravelWhereTwoOptAloneCannot()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateRelocateStrictImprovementScenario();
        var service = new ItineraryGenerationService(new FixedRouteDurationProvider(matrix));

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Relocate moves item 102 to tail: [101, 103, 104, 102]
        var visitIds = result.Value.Items
            .Where(it => it.Kind == ItineraryItemKind.Visit)
            .Select(it => it.PointOfInterestId)
            .ToList();
        visitIds.Should().Equal(101L, 103L, 104L, 102L);

        // Verify optimizer metrics directly: relocate executed and reduced travel
        var evaluator = new ItineraryScheduleEvaluator();
        var optimizer = new OptionalRouteOptimizer(evaluator);
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        var mandatoryCandidates = input.MandatoryPoiIds.Select(id => matrixCandidates.First(c => c.Id == id)).ToArray();
        var optResult = optimizer.Optimize(input, mandatoryCandidates, matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);

        optResult.Should().NotBeNull();
        optResult!.RelocateMovesCount.Should().BeGreaterThan(0);
        optResult.Schedule.TotalMatrixTravelMinutes.Should().BeLessThan(optResult.BaselineSchedule.TotalMatrixTravelMinutes);
        optResult.Schedule.VisitPoiIds.Should().BeEquivalentTo(optResult.BaselineSchedule.VisitPoiIds);
    }

    [Fact]
    public async Task GenerateAsync_SkippedCandidate_ReconsideredAndAdmittedAfterLocalImprovement()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateReconsiderationScenario();

        var disabledService = new ItineraryGenerationService(
            new FixedRouteDurationProvider(matrix),
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = false });

        var enabledService = new ItineraryGenerationService(
            new FixedRouteDurationProvider(matrix),
            options: new SchedulingGenerationOptions { EnableOptionalRouteOptimization = true });

        var disabledResult = await disabledService.GenerateAsync(input, CancellationToken.None);
        var enabledResult = await enabledService.GenerateAsync(input, CancellationToken.None);

        disabledResult.IsSuccess.Should().BeTrue();
        enabledResult.IsSuccess.Should().BeTrue();

        // In disabled mode, optional 205 is omitted because initial route travel exceeds available budget
        disabledResult.Value.Items
            .Where(it => it.Kind == ItineraryItemKind.Visit)
            .Select(it => it.PointOfInterestId)
            .Should().NotContain(205L);

        // In enabled mode, local improvement cuts travel and reconsideration admits 205
        enabledResult.Value.Items
            .Where(it => it.Kind == ItineraryItemKind.Visit)
            .Select(it => it.PointOfInterestId)
            .Should().Contain(205L);

        // Verify optimizer directly recorded reconsidered admission
        var evaluator = new ItineraryScheduleEvaluator();
        var optimizer = new OptionalRouteOptimizer(evaluator);
        var matrixCandidates = input.Candidates.ToArray();
        var candidateMatrixIndices = matrixCandidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);
        var mandatoryCandidates = input.MandatoryPoiIds.Select(id => matrixCandidates.First(c => c.Id == id)).ToArray();
        var optResult = optimizer.Optimize(input, mandatoryCandidates, matrix, matrixCandidates, candidateMatrixIndices, CancellationToken.None);

        optResult.Should().NotBeNull();
        optResult!.ReconsideredAdmissionsCount.Should().BeGreaterThan(0);
        optResult.Schedule.VisitPoiIds.Should().Contain(205L);
        optResult.BaselineSchedule.VisitPoiIds.Should().NotContain(205L);
    }

    [Fact]
    public void ScheduleGlobalComparator_WhenMembershipAndObjectivesTie_UsesVisitIdsNotRankingPosition()
    {
        var higherRanked = OptionalRouteOptimizationScenarios.CreateCandidate(
            20,
            "Higher ranked",
            30,
            10_000m,
            1m);
        var lowerRanked = OptionalRouteOptimizationScenarios.CreateCandidate(
            10,
            "Lower ranked",
            30,
            10_000m,
            0.5m);
        var comparator = new ScheduleGlobalComparator([higherRanked, lowerRanked]);
        var endAtUtc = OptionalRouteOptimizationScenarios.StartAtUtc.AddHours(2);
        var emptyPlan = new GeneratedItineraryPlan([], endAtUtc, 120, 20_000m);
        var idOrdered = new EvaluatedItinerarySchedule(
            emptyPlan,
            TotalMatrixTravelMinutes: 40,
            TotalDurationMinutes: 120,
            endAtUtc,
            TotalEstimatedCost: 20_000m,
            VisitPoiIds: [10, 20]);
        var rankOrdered = idOrdered with { VisitPoiIds = [20, 10] };

        comparator.Compare(idOrdered, rankOrdered).Should().BeNegative();
    }


    private static GenerationInput CreateInput(
        int availableMinutes,
        RestPreference restPreference,
        IReadOnlyCollection<GenerationCandidate> candidates,
        IReadOnlyCollection<long> mandatoryPoiIds,
        decimal? budgetVnd = 200_000m) =>
        new(
            StartAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            new RoutePoint(16.0544m, 108.2022m),
            new RoutePoint(16.0600m, 108.2300m),
            availableMinutes,
            TransportMode.Motorbike,
            restPreference,
            budgetVnd,
            candidates,
            mandatoryPoiIds);

    private static readonly GenerationOpeningHours[] DefaultPermissiveTuesdayHours =
    [
        new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0)),
    ];

    private static GenerationCandidate Candidate(
        long id,
        string name,
        int visitDurationMinutes,
        decimal? cost,
        decimal tripMateBaseScore = 0m,
        decimal effectiveDesirabilityScore = 0m,
        decimal? scenicScoreForRanking = null,
        decimal? photoRatingForRanking = null,
        decimal? estimatedVisitCostForRanking = null,
        GenerationOpeningHours[]? openingHours = null) =>
        new(
            id,
            name,
            new RoutePoint(16m + (id / 10_000m), 108m + (id / 10_000m)),
            visitDurationMinutes,
            cost,
            openingHours ?? DefaultPermissiveTuesdayHours,
            tripMateBaseScore,
            effectiveDesirabilityScore,
            scenicScoreForRanking,
            photoRatingForRanking,
            estimatedVisitCostForRanking);

    private sealed class FixedRouteDurationProvider(RouteDurationMatrix matrix)
        : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            Task.FromResult(matrix);
    }

    private sealed class ThrowingRouteDurationProvider : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Provider must not be called for invalid input.");
    }

    private sealed class CountingRouteDurationProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public int CallCount { get; private set; }

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(matrix);
        }
    }
}
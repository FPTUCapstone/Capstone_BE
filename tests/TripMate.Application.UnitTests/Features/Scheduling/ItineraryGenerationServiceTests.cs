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
    public async Task Generate_AddsMultipleOptionalPoisInDeterministicPreferenceOrder()
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
            .Should().Equal(28L, 12L);
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
            availableMinutes: 240,
            restPreference: RestPreference.None,
            candidates: [first, second],
            mandatoryPoiIds: []);

        var result = await service.GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit)
            .Select(item => item.PointOfInterestId)
            .Should().StartWith(expectedFirstId);
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
}

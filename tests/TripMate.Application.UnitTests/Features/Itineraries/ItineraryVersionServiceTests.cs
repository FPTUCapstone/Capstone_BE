using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Itineraries.AdjustItems;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Itineraries.List;
using TripMate.Application.Features.Itineraries.Regenerate;
using TripMate.Application.Features.Itineraries.Suggestions;
using TripMate.Application.Features.PointsOfInterest.ExternalReviews;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class ItineraryVersionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Regenerate_UsesSchedulingRequestAsTheMutationLockResource()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAfterTwoUnrelatedRequestsAsync(dbContext);
        var itinerary = await AddItineraryAsync(dbContext, request);
        var mutationLock = new RecordingItineraryMutationLock();
        var handler = new RegenerateItineraryCommandHandler(
            dbContext,
            new ItineraryAccessService(dbContext),
            new ItineraryVersionService(dbContext, new FixedRouteDurationProvider()),
            mutationLock,
            new FakeDateTimeProvider { UtcNow = Now });

        _ = await handler.Handle(
            new RegenerateItineraryCommand(itinerary.Id, request.TravelerUserId, Guid.NewGuid()),
            CancellationToken.None);

        mutationLock.AcquiredResources.Should().Equal(request.Id);
        request.Id.Should().NotBe(itinerary.Id);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_UsesSavedTravelerInterestTagsToRankOptionalPois()
    {
        await using var dbContext = TestDbContext.Create();
        const long travelerUserId = 42;
        var request = await AddRequestAsync(dbContext, travelerUserId);
        var original = await AddItineraryAsync(dbContext, request);
        var preferred = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Preferred museum");
        _ = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Other attraction");
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            travelerUserId,
            "[\"culture\"]",
            Now));
        await dbContext.SaveChangesAsync();

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .Where(item => item.PointOfInterestId.HasValue)
            .Select(item => item.PointOfInterestId)
            .Should().StartWith(preferred.Id);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_SuggestsAnotherPlaceInsteadOfTheCurrentStop()
    {
        // Only one five-hour visit fits the eight-hour trip, and the current one is the best ranked.
        await using var dbContext = TestDbContext.Create();
        const long travelerUserId = 42;
        var request = await AddRequestAsync(dbContext, travelerUserId);
        var current = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Current museum", visitMinutes: 300);
        var other = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Other park", visitMinutes: 300);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(travelerUserId, "[\"culture\"]", Now));
        var original = await AddItineraryAsync(dbContext, request, current.Id);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().Equal(other.Id);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_InWeightedUtilityMode_SuggestsAnotherPlaceWithoutThrowing()
    {
        await using var dbContext = TestDbContext.Create();
        const long travelerUserId = 42;
        var request = await AddRequestAsync(dbContext, travelerUserId);
        var current = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Current museum", visitMinutes: 300);
        var other = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Other park", visitMinutes: 300);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(travelerUserId, "[\"culture\"]", Now));
        var original = await AddItineraryAsync(dbContext, request, current.Id);
        var options = new SchedulingGenerationOptions
        {
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions { Mode = ObjectiveMode.WeightedUtility },
        };

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider(),
                options,
                new TimeOfDayOptions { Enabled = false })
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().Equal(other.Id);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_InWeightedUtilityMode_ColdStartUsesPreviousStopToFillAvailableTime()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var previous = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Previous museum", visitMinutes: 60);
        var newPoi = await AddPlanningReadyPoiAsync(dbContext, "Nature", "New park", visitMinutes: 60);
        var original = await AddItineraryAsync(dbContext, request, previous.Id);
        var options = new SchedulingGenerationOptions
        {
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions { Mode = ObjectiveMode.WeightedUtility },
        };

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider(),
                options,
                new TimeOfDayOptions { Enabled = false })
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().Equal(newPoi.Id, previous.Id);
    }

    [Theory]
    [InlineData("nature")]
    [InlineData("culture")]
    public async Task CreateRegeneratedVersion_InWeightedUtilityMode_WithPreferencesUsesPreviousStopToFillAvailableTime(
        string interestTag)
    {
        await using var dbContext = TestDbContext.Create();
        const long travelerUserId = 42;
        var request = await AddRequestAsync(dbContext, travelerUserId);
        var previous = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Previous museum", visitMinutes: 60);
        var newPoi = await AddPlanningReadyPoiAsync(dbContext, "Nature", "New park", visitMinutes: 60);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(travelerUserId, $"[\"{interestTag}\"]", Now));
        var original = await AddItineraryAsync(dbContext, request, previous.Id);
        var options = new SchedulingGenerationOptions
        {
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions { Mode = ObjectiveMode.WeightedUtility },
        };

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider(),
                options,
                new TimeOfDayOptions { Enabled = false })
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().Equal(newPoi.Id, previous.Id);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_InWeightedUtilityMode_WhenNoOtherPlaceFits_ReturnsNoAlternative()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var only = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Only museum");
        var original = await AddItineraryAsync(dbContext, request, only.Id);
        var options = new SchedulingGenerationOptions
        {
            SolverMode = SchedulingSolverMode.Csp,
            Csp = new CspOptions { Mode = ObjectiveMode.WeightedUtility },
        };

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider(),
                options,
                new TimeOfDayOptions { Enabled = false })
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ItineraryErrorCodes.NoAlternative);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_PrefersAPlaceNeverSuggestedOverOneFromAnEarlierVersion()
    {
        // v1 showed the museum, v2 (current) the park: v3 must not bounce back to the museum
        // while the garden has not been suggested yet.
        await using var dbContext = TestDbContext.Create();
        const long travelerUserId = 42;
        var request = await AddRequestAsync(dbContext, travelerUserId);
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Museum v1", visitMinutes: 300);
        var park = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Park v2", visitMinutes: 300);
        var garden = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Garden never shown", visitMinutes: 300);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(travelerUserId, "[\"culture\"]", Now));
        _ = await AddItineraryAsync(dbContext, request, version: 1, museum.Id);
        var current = await AddItineraryAsync(dbContext, request, version: 2, park.Id);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(current, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().Equal(garden.Id);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_ReusesCurrentStopsToFillTimeLeft()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var current = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Current museum");
        var other = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Other park");
        var original = await AddItineraryAsync(dbContext, request, current.Id);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().BeEquivalentTo([other.Id, current.Id]);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_WhenNoOtherPlaceFits_KeepsTheCurrentVersion()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var only = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Only museum");
        var original = await AddItineraryAsync(dbContext, request, only.Id);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ItineraryErrorCodes.NoAlternative);
        dbContext.ChangeTracker.Entries<Itinerary>()
            .Should().NotContain(entry => entry.State == EntityState.Added);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_PersistsRoadDurationForTheNextItineraryItem()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request);
        _ = await AddPlanningReadyPoiAsync(dbContext, "Culture", "First stop");
        _ = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Second stop");

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items
            .OrderBy(item => item.SequenceNo)
            .First()
            .TravelDurationToNextMinutes
            .Should().Be(10);
    }

    [Theory]
    [InlineData(PoiCategoryNames.FoodAndDrink)]
    [InlineData("Cafe")]
    public async Task CreateRegeneratedVersion_NeverAutoSchedulesFoodAndDrink(string categoryName)
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request);
        var restaurant = await AddPlanningReadyPoiAsync(dbContext, categoryName, "Mi Quang Ba Mua");
        _ = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.PointOfInterestId).Should().NotContain(restaurant.Id);
    }

    [Fact]
    public async Task CreateAdjustedVersion_AcceptsAPlaceThatWasNotInTheItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var bridge = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Dragon Bridge", 16.0612m, 108.2277m);
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request, 1, museum.Id);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(original, request, [new ItineraryStop(museum.Id), new ItineraryStop(bridge.Id)], CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().Equal(museum.Id, bridge.Id);
    }

    [Fact]
    public async Task CreateAdjustedVersion_ChosenPlaceRankedBelowTheMatrixCap_IsStillScheduled()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        for (var index = 0; index < 8; index++)
        {
            _ = await AddPlanningReadyPoiAsync(dbContext, "Culture", $"Filler {index}", 16.0471m + (index / 10_000m), 108.2068m);
        }

        // Most expensive, so it ranks last and falls outside a cap of 6 unless the traveler chose it.
        var chosen = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Chosen gallery", 16.0480m, 108.2070m);
        chosen.ConfigurePlanningMetadata(500_000m, "https://example.com/chosen", Now);
        await dbContext.SaveChangesAsync();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request, 1, museum.Id);
        var options = new SchedulingGenerationOptions { MaxMatrixCandidates = SchedulingGenerationOptions.MinimumMaxMatrixCandidates };

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider(), options)
            .CreateAdjustedVersionAsync(original, request, [new ItineraryStop(museum.Id), new ItineraryStop(chosen.Id)], CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().Equal(museum.Id, chosen.Id);
    }

    [Fact]
    public async Task CreateAdjustedVersion_PlaceOutsideTheSearchArea_IsRejectedWithItsReason()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var faraway = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Hue Citadel", 16.4698m, 107.5786m);
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request, 1, museum.Id);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(original, request, [new ItineraryStop(museum.Id), new ItineraryStop(faraway.Id)], CancellationToken.None);

        result.ErrorCode.Should().Be(ItineraryErrorCodes.StopInfeasible);
        result.ErrorMetadata["poiId"].Should().Be(faraway.Id);
        result.ErrorMetadata["reason"].Should().Be(StopInfeasibilityReasons.OutsideRadius);
    }

    [Fact]
    public async Task CreateAdjustedVersion_InactivePlace_IsRejectedAsInactive()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var closed = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Closed gallery");
        dbContext.Entry(closed).Property(poi => poi.Status).CurrentValue = PointOfInterestStatus.Inactive;
        await dbContext.SaveChangesAsync();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request, 1, museum.Id);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(original, request, [new ItineraryStop(museum.Id), new ItineraryStop(closed.Id)], CancellationToken.None);

        result.ErrorCode.Should().Be(ItineraryErrorCodes.StopInfeasible);
        result.ErrorMetadata["reason"].Should().Be(StopInfeasibilityReasons.Inactive);
    }

    [Fact]
    public async Task CreateAdjustedVersion_PersistsTheTransportChosenForALeg()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var bridge = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Dragon Bridge", 16.0612m, 108.2277m);
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request, 1, museum.Id, bridge.Id);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(
                original,
                request,
                [new ItineraryStop(museum.Id, TransportMode.Walking), new ItineraryStop(bridge.Id)],
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var museumItem = result.Value.Items.Single(item => item.PointOfInterestId == museum.Id);
        museumItem.TransportModeToNext.Should().Be(TransportMode.Walking);
    }

    [Fact]
    public async Task CreateAdjustedVersion_AfterTheTravelerDropsAMandatoryPlace_Succeeds()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var bridge = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Dragon Bridge", 16.0612m, 108.2277m);
        var request = await AddRequestAsync(dbContext, travelerUserId: 42, mandatoryPoiIdsJson: $"[{museum.Id}]");
        var original = await AddItineraryAsync(dbContext, request, 1, museum.Id);

        request.KeepOnlyMandatoryPois([bridge.Id]);
        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(original, request, [new ItineraryStop(bridge.Id)], CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().Equal(bridge.Id);
        request.MandatoryPoiIdsJson.Should().Be("[]");
    }

    [Fact]
    public async Task CreateAdjustedVersion_KeepsAFoodAndDrinkStopTheTravelerChose()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var restaurant = await AddPlanningReadyPoiAsync(dbContext, PoiCategoryNames.FoodAndDrink, "Mi Quang Ba Mua");
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(original, request, [museum.Id, restaurant.Id], CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.PointOfInterestId).Should().Contain(restaurant.Id);
    }

    [Fact]
    public async Task CreateAdjustedVersion_WhenStopsExceedEndTime_AutoExtendsAndSucceeds()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var bridge = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Dragon Bridge", 16.0612m, 108.2277m);
        var request = CreateRequest(42, Guid.NewGuid(), availableMinutes: 75);
        var generationOwnerId = Guid.NewGuid();
        request.ClaimGeneration(generationOwnerId, Now.AddMinutes(1), Now);
        request.CompleteGeneration(generationOwnerId, Now);
        dbContext.SchedulingRequests.Add(request);
        await dbContext.SaveChangesAsync();

        var original = await AddItineraryAsync(dbContext, request, 1, museum.Id);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(
                original,
                request,
                [new ItineraryStop(museum.Id), new ItineraryStop(bridge.Id)],
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ValidToUtc.Should().BeAfter(request.StartAtUtc.AddMinutes(75));
    }

    [Fact]
    public async Task CreateRegeneratedVersion_PlacesOutdoorStopsInTheMorningAndIndoorOnesAtNoon()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request);
        var museum = PointOfInterest.Create(
            PoiCategory.Create("Museum", null), "Cham Museum", 16.0471m, 108.2068m, 1, Now,
            indoorOutdoor: IndoorOutdoorType.Indoor, averageVisitDurationMinutes: 180, hasShelter: true);
        museum.ConfigurePlanningMetadata(60_000m, "https://example.com/museum", Now);
        museum.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
        dbContext.PointsOfInterest.Add(museum);
        await dbContext.SaveChangesAsync();
        // Trip starts 09:00; legs are 10 min + 10 min buffer, so a 10-minute visit is centred at 09:25 (morning).
        var mountain = await AddPlanningReadyPoiAsync(dbContext, "Natural attraction", "Marble Mountains", visitMinutes: 10);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitIds(result.Value).Should().Equal(mountain.Id, museum.Id);
        result.Value.Items.Single(item => item.PointOfInterestId == mountain.Id).RecommendationReason
            .Should().Be("Xếp vào buổi sáng khi trời mát, ít đông");
        result.Value.Items.Single(item => item.PointOfInterestId == museum.Id).RecommendationReason
            .Should().Be("Xếp vào buổi trưa để tránh nắng");
    }

    [Fact]
    public async Task CreateRegeneratedVersion_PersistsRoadDistanceAndTransportModeOfEachLeg()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request);
        _ = await AddPlanningReadyPoiAsync(dbContext, "Culture", "First stop");
        _ = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Second stop");

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        var items = result.Value.Items.OrderBy(item => item.SequenceNo).ToArray();
        items[0].TravelDistanceToNextMeters.Should().Be(4000);
        items[0].TransportModeToNext.Should().Be(request.TransportMode);
        items[^1].TravelDistanceToNextMeters.Should().BeNull();
        items[^1].TransportModeToNext.Should().BeNull();
    }

    [Fact]
    public async Task CreateAdjustedVersion_PersistsRoadDistanceAndTransportModeOfEachLeg()
    {
        await using var dbContext = TestDbContext.Create();
        var first = await AddPlanningReadyPoiAsync(dbContext, "Culture", "First stop");
        var second = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Second stop");
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var original = await AddItineraryAsync(dbContext, request);

        var result = await new ItineraryVersionService(dbContext, new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(original, request, [first.Id, second.Id], CancellationToken.None);

        var items = result.Value.Items.OrderBy(item => item.SequenceNo).ToArray();
        items[0].TravelDistanceToNextMeters.Should().Be(4000);
        items[0].TransportModeToNext.Should().Be(request.TransportMode);
        items[^1].TravelDistanceToNextMeters.Should().BeNull();
    }

    [Fact]
    public async Task CreateAdjustedVersion_OptionalStopKeepsOptionalFlagAndWording()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Mandatory museum");
        var optionalPoi = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Optional museum");
        var request = await AddRequestAsync(
            dbContext,
            travelerUserId: 42,
            mandatoryPoiIdsJson: $"[{mandatoryPoi.Id}]");
        var original = await AddItineraryAsync(dbContext, request);

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(
                original,
                request,
                [mandatoryPoi.Id, optionalPoi.Id],
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var mandatoryItem = result.Value.Items.Single(item => item.PointOfInterestId == mandatoryPoi.Id);
        mandatoryItem.IsMandatory.Should().BeTrue();
        mandatoryItem.RecommendationReason.Should().Be("Mandatory location");
        var optionalItem = result.Value.Items.Single(item => item.PointOfInterestId == optionalPoi.Id);
        optionalItem.IsMandatory.Should().BeFalse();
        optionalItem.RecommendationReason.Should().NotBe("Mandatory location");
        optionalItem.RecommendationReason.Should().Be("Suggested nearby location");
    }

    [Fact]
    public async Task CreateRegeneratedVersion_LimitsRouteMatrixToConfiguredCandidateCap()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, 42);
        var original = await AddItineraryAsync(dbContext, request);
        for (var index = 0; index < 45; index++)
        {
            _ = await AddPlanningReadyPoiAsync(dbContext, "Culture", $"POI {index:00}");
        }

        var provider = new RecordingRouteDurationProvider();
        var result = await new ItineraryVersionService(dbContext, provider)
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        provider.LastPointCount.Should().Be(SchedulingGenerationOptions.DefaultMaxMatrixCandidates + 2);
    }

    [Fact]
    public async Task CreateRegeneratedVersion_MandatoryCandidateSurvivesRouteMatrixCap()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await AddPlanningReadyPoiAsync(
            dbContext,
            "Culture",
            "Mandatory boundary POI",
            latitude: 16.05m,
            longitude: 108.21m);
        var request = await AddRequestAsync(dbContext, 42, $"[{mandatoryPoi.Id}]");
        var original = await AddItineraryAsync(dbContext, request);
        for (var index = 0; index < 45; index++)
        {
            _ = await AddPlanningReadyPoiAsync(dbContext, "Culture", $"Optional POI {index:00}");
        }

        var provider = new RecordingRouteDurationProvider();
        var result = await new ItineraryVersionService(dbContext, provider)
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        provider.LastPointCount.Should().Be(SchedulingGenerationOptions.DefaultMaxMatrixCandidates + 2);
        provider.LastPoints.Should().Contain(new RoutePoint(mandatoryPoi.Latitude, mandatoryPoi.Longitude));
    }

    [Fact]
    public async Task CreateRegeneratedVersion_WhenPublicTransitIsStored_ReturnsControlledInfeasibleResult()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, 42, transportMode: TransportMode.PublicTransit);
        var original = await AddItineraryAsync(dbContext, request);
        _ = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Museum");
        var provider = new RecordingRouteDurationProvider();

        var result = await new ItineraryVersionService(dbContext, provider)
            .CreateRegeneratedVersionAsync(original, request, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ItineraryErrorCodes.ConstraintsInfeasible);
        provider.LastPointCount.Should().BeNull();
    }

    [Fact]
    public async Task CreateAdjustedVersion_WhenPublicTransitIsStored_ReturnsControlledInfeasibleResult()
    {
        await using var dbContext = TestDbContext.Create();
        var request = await AddRequestAsync(dbContext, 42, transportMode: TransportMode.PublicTransit);
        var original = await AddItineraryAsync(dbContext, request);
        var provider = new RecordingRouteDurationProvider();

        var result = await new ItineraryVersionService(dbContext, provider)
            .CreateAdjustedVersionAsync(original, request, Array.Empty<long>(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ItineraryErrorCodes.ConstraintsInfeasible);
        provider.LastPointCount.Should().BeNull();
    }

    [Fact]
    public async Task CreateAdjustedVersion_DistantPoiOutsideBoundingBox_IsNotConsideredAsCandidate()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Mandatory museum");
        var distantPoi = await AddPlanningReadyPoiAsync(
            dbContext,
            "Culture",
            "Distant Hanoi museum",
            latitude: 21.0285m,
            longitude: 105.8542m);
        var request = await AddRequestAsync(
            dbContext,
            42,
            mandatoryPoiIdsJson: $"[{mandatoryPoi.Id}]");
        var original = await AddItineraryAsync(dbContext, request);

        var result = await new ItineraryVersionService(
                dbContext,
                new FixedRouteDurationProvider())
            .CreateAdjustedVersionAsync(
                original,
                request,
                [mandatoryPoi.Id, distantPoi.Id],
                CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ItineraryErrorCodes.StopInfeasible);
        result.ErrorMetadata["poiId"].Should().Be(distantPoi.Id);
        result.ErrorMetadata["reason"].Should().Be(StopInfeasibilityReasons.OutsideRadius);
    }

    private static async Task<SchedulingRequest> AddRequestAfterTwoUnrelatedRequestsAsync(TestDbContext dbContext)
    {
        dbContext.SchedulingRequests.AddRange(
            CreateRequest(40, Guid.NewGuid()),
            CreateRequest(41, Guid.NewGuid()));
        await dbContext.SaveChangesAsync();
        return await AddRequestAsync(dbContext, 42);
    }

    private static async Task<SchedulingRequest> AddRequestAsync(
        TestDbContext dbContext,
        long travelerUserId,
        string mandatoryPoiIdsJson = "[]",
        TransportMode transportMode = TransportMode.Motorbike)
    {
        var request = CreateRequest(travelerUserId, Guid.NewGuid(), mandatoryPoiIdsJson, transportMode);
        var generationOwnerId = Guid.NewGuid();
        request.ClaimGeneration(generationOwnerId, Now.AddMinutes(1), Now);
        request.CompleteGeneration(generationOwnerId, Now);
        dbContext.SchedulingRequests.Add(request);
        await dbContext.SaveChangesAsync();
        return request;
    }

    private static SchedulingRequest CreateRequest(
        long travelerUserId,
        Guid idempotencyKey,
        string mandatoryPoiIdsJson = "[]",
        TransportMode transportMode = TransportMode.Motorbike,
        int availableMinutes = 480) =>
        SchedulingRequest.Create(
            travelerUserId,
            idempotencyKey,
            new string('a', SchedulingRequest.RequestHashLength),
            Now.AddHours(1),
            "Asia/Ho_Chi_Minh",
            16.0471m,
            108.2068m,
            16.0471m,
            108.2068m,
            null,
            true,
            availableMinutes,
            transportMode,
            10m,
            800_000m,
            mandatoryPoiIdsJson,
            RestPreference.None,
            Now);

    private static long[] VisitIds(Itinerary itinerary) => itinerary.Items
        .Where(item => item.Kind == ItineraryItemKind.Visit)
        .OrderBy(item => item.SequenceNo)
        .Select(item => item.PointOfInterestId!.Value)
        .ToArray();

    [Fact]
    public async Task Suggestions_Food_ListsRestaurantsThatFitFirstAndGreysOutClosedOnes()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var openRestaurant = await AddPlanningReadyPoiAsync(dbContext, "Cafe", "Mi Quang Ba Mua", 16.0480m, 108.2070m);
        openRestaurant.LinkTripadvisorLocation(8_064_453);
        var breakfastOnly = PointOfInterest.Create(
            PoiCategory.Create(PoiCategoryNames.FoodAndDrink, null), "Breakfast stall", 16.0472m, 108.2069m, 1, Now,
            averageVisitDurationMinutes: 45);
        breakfastOnly.ConfigurePlanningMetadata(30_000m, "https://example.com/breakfast", Now);
        breakfastOnly.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(6, 0), new TimeOnly(9, 30), false));
        dbContext.PointsOfInterest.Add(breakfastOnly);
        await dbContext.SaveChangesAsync();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, museum.Id);
        var museumItemId = itinerary.Items.Single().Id;

        var result = await CreateSuggestionsHandler(dbContext).Handle(
            new GetItinerarySuggestionsQuery(itinerary.Id, 42, GetItinerarySuggestionsQuery.KindFood, museumItemId, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.PoiId).Should().Equal(openRestaurant.Id, breakfastOnly.Id);
        var fits = result.Value.Items[0];
        fits.FitsItinerary.Should().BeTrue();
        fits.CostKind.Should().Be(CostKind.AverageSpend);
        fits.DistanceSource.Should().Be(SuggestionDistanceSources.Road);
        fits.DistanceFromPreviousMeters.Should().Be(4000);
        fits.DetourMinutes.Should().BePositive();
        fits.Tripadvisor!.Rating.Should().Be(3.7m);
        var closed = result.Value.Items[1];
        closed.FitsItinerary.Should().BeFalse();
        closed.OpenAtArrival.Should().BeFalse();
        closed.UnfitReason.Should().Be(StopInfeasibilityReasons.ClosedAtArrival);
        closed.ClosesAt.Should().Be("09:30");
    }

    [Fact]
    public async Task Suggestions_Replace_OffersOnlyPlacesOfTheSameCategory()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var gallery = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Fine Arts Museum", 16.0480m, 108.2070m);
        _ = await AddPlanningReadyPoiAsync(dbContext, "Cafe", "Mi Quang Ba Mua", 16.0481m, 108.2071m);
        _ = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Son Tra", 16.0482m, 108.2072m);
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, museum.Id);

        var result = await CreateSuggestionsHandler(dbContext).Handle(
            new GetItinerarySuggestionsQuery(
                itinerary.Id, 42, GetItinerarySuggestionsQuery.KindReplace, null, itinerary.Items.Single().Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.PoiId).Should().Equal(gallery.Id);
        result.Value.Items[0].CostKind.Should().Be(CostKind.AdmissionTicket);
    }

    [Fact]
    public async Task Suggestions_Replace_WhenSameCategoryExhausted_OffersOtherAttractionsInRadius()
    {
        await using var dbContext = TestDbContext.Create();
        var natureStop = await AddPlanningReadyPoiAsync(dbContext, "Nature", "Apec Park");
        _ = await AddPlanningReadyPoiAsync(dbContext, PoiCategoryNames.FoodAndDrink, "Mi Quang Ba Mua", 16.0481m, 108.2071m);
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Museum", "Cham Museum", 16.0482m, 108.2072m);
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, natureStop.Id);

        var result = await CreateSuggestionsHandler(dbContext).Handle(
            new GetItinerarySuggestionsQuery(
                itinerary.Id, 42, GetItinerarySuggestionsQuery.KindReplace, null, itinerary.Items.Single().Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.PoiId).Should().Equal(museum.Id);
    }

    [Fact]
    public async Task Suggestions_ByAnotherTraveler_AreDenied()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, museum.Id);

        var result = await CreateSuggestionsHandler(dbContext).Handle(
            new GetItinerarySuggestionsQuery(itinerary.Id, 99, GetItinerarySuggestionsQuery.KindFood, itinerary.Items.Single().Id, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Suggestions_ReplaceAMandatoryStop_OffersPlacesThatFit()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var gallery = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Fine Arts Museum", 16.0480m, 108.2070m);
        var request = await AddRequestAsync(dbContext, travelerUserId: 42, mandatoryPoiIdsJson: $"[{museum.Id}]");
        var itinerary = await AddItineraryAsync(dbContext, request, 1, museum.Id);

        var result = await CreateSuggestionsHandler(dbContext).Handle(
            new GetItinerarySuggestionsQuery(
                itinerary.Id, 42, GetItinerarySuggestionsQuery.KindReplace, null, itinerary.Items.Single().Id),
            CancellationToken.None);

        result.Value.Items.Should().ContainSingle(item => item.PoiId == gallery.Id)
            .Which.FitsItinerary.Should().BeTrue();
    }

    [Fact]
    public async Task Suggestions_WhenTheSuggestionMakesALaterStopMissItsClosingTime_NamesThatStop()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var bridge = PointOfInterest.Create(
            PoiCategory.Create("Culture", null), "Morning market", 16.0473m, 108.2069m, 1, Now,
            averageVisitDurationMinutes: 30);
        bridge.ConfigurePlanningMetadata(0m, "https://example.com/market", Now);
        bridge.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(11, 15), false));
        dbContext.PointsOfInterest.Add(bridge);
        var restaurant = await AddPlanningReadyPoiAsync(dbContext, PoiCategoryNames.FoodAndDrink, "Mi Quang Ba Mua", 16.0480m, 108.2070m);
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, museum.Id, bridge.Id);
        var museumItemId = itinerary.Items.Single(item => item.PointOfInterestId == museum.Id).Id;

        var result = await CreateSuggestionsHandler(dbContext).Handle(
            new GetItinerarySuggestionsQuery(itinerary.Id, 42, GetItinerarySuggestionsQuery.KindFood, museumItemId, null),
            CancellationToken.None);

        var suggestion = result.Value.Items.Single(item => item.PoiId == restaurant.Id);
        suggestion.FitsItinerary.Should().BeFalse();
        suggestion.OpenAtArrival.Should().BeTrue();
        suggestion.UnfitReason.Should().Be(StopInfeasibilityReasons.ClosedAtArrival);
        suggestion.UnfitPoiId.Should().Be(bridge.Id);
        suggestion.ClosesAt.Should().Be("20:00");
    }

    [Fact]
    public async Task Suggestions_Food_WhenTheItineraryIsFull_OffersNothingThatCouldNotBeSaved()
    {
        await using var dbContext = TestDbContext.Create();
        var stops = new List<long>();
        for (var index = 0; index < AdjustItineraryItemsCommand.MaxStops; index++)
        {
            stops.Add((await AddPlanningReadyPoiAsync(dbContext, "Culture", $"Stop {index}", 16.0471m + (index / 10_000m), 108.2068m)).Id);
        }

        _ = await AddPlanningReadyPoiAsync(dbContext, PoiCategoryNames.FoodAndDrink, "Mi Quang Ba Mua", 16.0480m, 108.2070m);
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, [.. stops]);

        var result = await CreateSuggestionsHandler(dbContext).Handle(
            new GetItinerarySuggestionsQuery(
                itinerary.Id, 42, GetItinerarySuggestionsQuery.KindFood, itinerary.Items.First().Id, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Suggestions_ForAGroupMemberWhoCannotEdit_AreDenied()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, museum.Id);
        var group = TravelGroup.Create(itinerary.Id, 42, "Family trip", Now);
        group.AddMember(GroupMember.CreateHost(group, 42, Now));
        group.AddMember(GroupMember.CreateMember(group, 77, Now));
        dbContext.TravelGroups.Add(group);
        await dbContext.SaveChangesAsync();

        var result = await CreateSuggestionsHandler(dbContext).Handle(
            new GetItinerarySuggestionsQuery(itinerary.Id, 77, GetItinerarySuggestionsQuery.KindFood, itinerary.Items.Single().Id, null),
            CancellationToken.None);

        result.ErrorCode.Should().Be(ItineraryErrorCodes.OwnerPermissionRequired);
    }

    [Fact]
    public async Task ItineraryAccess_WhenClosedRowRetainsActiveMember_DoesNotGrantGroupAccess()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, museum.Id);
        var group = TravelGroup.Create(itinerary.Id, 42, "Closed family trip", Now);
        var host = GroupMember.CreateHost(group, 42, Now);
        var member = GroupMember.CreateMember(group, 77, Now);
        group.AddMember(host);
        group.AddMember(member);
        dbContext.TravelGroups.Add(group);
        await dbContext.SaveChangesAsync();

        typeof(TravelGroup).GetProperty(nameof(TravelGroup.Status))!
            .SetValue(group, TravelGroupStatus.Closed);
        typeof(TravelGroup).GetProperty(nameof(TravelGroup.ClosedAtUtc))!
            .SetValue(group, Now);
        await dbContext.SaveChangesAsync();

        var access = await new ItineraryAccessService(dbContext).ResolveCurrentAsync(
            itinerary.Id, 77, trackCurrent: false, CancellationToken.None);

        access.IsFailure.Should().BeTrue();
        access.ErrorCode.Should().Be(ItineraryErrorCodes.AccessDenied);
    }

    [Fact]
    public async Task ListItineraries_WhenGroupCloses_HidesPlanFromNonOwner()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, museum.Id);
        var group = TravelGroup.Create(itinerary.Id, 42, "Closed family trip", Now);
        group.AddMember(GroupMember.CreateHost(group, 42, Now));
        group.AddMember(GroupMember.CreateMember(group, 77, Now));
        dbContext.TravelGroups.Add(group);
        await dbContext.SaveChangesAsync();
        typeof(TravelGroup).GetProperty(nameof(TravelGroup.Status))!
            .SetValue(group, TravelGroupStatus.Closed);
        typeof(TravelGroup).GetProperty(nameof(TravelGroup.ClosedAtUtc))!
            .SetValue(group, Now);
        await dbContext.SaveChangesAsync();

        var handler = new ListMyItinerariesQueryHandler(dbContext, new FakeDateTimeProvider { UtcNow = Now });
        var memberResult = await handler.Handle(new ListMyItinerariesQuery(77, 1, 20), CancellationToken.None);
        var ownerResult = await handler.Handle(new ListMyItinerariesQuery(42, 1, 20), CancellationToken.None);

        memberResult.Value.Items.Should().BeEmpty();
        ownerResult.Value.Items.Should().ContainSingle(item => item.ItineraryId == itinerary.Id);
    }

    [Fact]
    public async Task Suggestions_Food_SayWhetherTripadvisorHasRecentReviewsToShow()
    {
        await using var dbContext = TestDbContext.Create();
        var museum = await AddPlanningReadyPoiAsync(dbContext, "Culture", "Cham Museum");
        var reviewed = await AddPlanningReadyPoiAsync(dbContext, PoiCategoryNames.FoodAndDrink, "Banh Xeo Ba Duong", 16.0480m, 108.2070m);
        reviewed.LinkTripadvisorLocation(6_961_132);
        var quiet = await AddPlanningReadyPoiAsync(dbContext, PoiCategoryNames.FoodAndDrink, "My Quang Que Xua", 16.0481m, 108.2071m);
        quiet.LinkTripadvisorLocation(9_739_777);
        await dbContext.SaveChangesAsync();
        var request = await AddRequestAsync(dbContext, travelerUserId: 42);
        var itinerary = await AddItineraryAsync(dbContext, request, 1, museum.Id);

        var result = await CreateSuggestionsHandler(dbContext, locationsWithReviews: [6_961_132]).Handle(
            new GetItinerarySuggestionsQuery(itinerary.Id, 42, GetItinerarySuggestionsQuery.KindFood, itinerary.Items.Single().Id, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Single(item => item.PoiId == reviewed.Id).Tripadvisor!.HasRecentReviews.Should().BeTrue();
        result.Value.Items.Single(item => item.PoiId == quiet.Id).Tripadvisor!.HasRecentReviews.Should().BeFalse();
    }

    private static GetItinerarySuggestionsQueryHandler CreateSuggestionsHandler(
        TestDbContext dbContext,
        IReadOnlyCollection<long>? locationsWithReviews = null) =>
        new(
            dbContext,
            new ItineraryAccessService(dbContext),
            new ItineraryVersionService(dbContext, new FixedRouteDurationProvider()),
            new FakeTripadvisorClient(locationsWithReviews ?? []));

    private sealed class FakeTripadvisorClient(IReadOnlyCollection<long> locationsWithReviews) : ITripadvisorClient
    {
        public bool IsEnabled => true;

        public Task<IReadOnlyDictionary<long, TripadvisorRating>> GetRatingsAsync(
            IReadOnlyCollection<long> locationIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<long, TripadvisorRating>>(locationIds.ToDictionary(
                id => id,
                id => new TripadvisorRating(id, 3.7m, 97, "https://ta/3.5.png", "https://ta/" + id)));

        public Task<IReadOnlyList<ExternalReviewDto>> GetReviewsAsync(
            long locationId,
            int size,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalReviewDto>>(locationsWithReviews.Contains(locationId)
                ? [new ExternalReviewDto(1, 5, "Great", "Crispy pancakes", "en", null, null, "traveler", null, null)]
                : []);
    }

    private static Task<Itinerary> AddItineraryAsync(
        TestDbContext dbContext,
        SchedulingRequest request,
        params long[] visitPoiIds) =>
        AddItineraryAsync(dbContext, request, version: 1, visitPoiIds);

    private static async Task<Itinerary> AddItineraryAsync(
        TestDbContext dbContext,
        SchedulingRequest request,
        int version,
        params long[] visitPoiIds)
    {
        var itinerary = Itinerary.CreateCspGenerated(
            request,
            "Generated trip",
            Now.AddHours(1),
            Now.AddHours(5),
            version);
        for (var index = 0; index < visitPoiIds.Length; index++)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                index + 1,
                visitPoiIds[index],
                Now.AddHours(1 + index),
                Now.AddHours(2 + index),
                false,
                60_000m,
                "Suggested nearby location"));
        }

        dbContext.Itineraries.Add(itinerary);
        await dbContext.SaveChangesAsync();
        return itinerary;
    }

    private static async Task<PointOfInterest> AddPlanningReadyPoiAsync(
        TestDbContext dbContext,
        string categoryName,
        string poiName,
        decimal latitude = 16.0471m,
        decimal longitude = 108.2068m,
        int visitMinutes = 60)
    {
        var poi = PointOfInterest.Create(
            PoiCategory.Create(categoryName, null),
            poiName,
            latitude,
            longitude,
            1,
            Now,
            averageVisitDurationMinutes: visitMinutes);
        poi.ConfigurePlanningMetadata(60_000m, "https://example.com/poi", Now);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            2,
            new TimeOnly(7, 0),
            new TimeOnly(20, 0),
            false));
        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();
        return poi;
    }

    private sealed class RecordingItineraryMutationLock : IItineraryMutationLock
    {
        public List<long> AcquiredResources { get; } = [];

        public Task AcquireAsync(long itineraryId, CancellationToken cancellationToken)
        {
            AcquiredResources.Add(itineraryId);
            return Task.CompletedTask;
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

    private sealed class RecordingRouteDurationProvider : IRouteDurationProvider
    {
        public int? LastPointCount { get; private set; }
        public IReadOnlyList<RoutePoint>? LastPoints { get; private set; }

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            LastPointCount = points.Count;
            LastPoints = points.ToArray();
            var durations = new int[points.Count, points.Count];
            return Task.FromResult(RouteDurationMatrix.Create(durations));
        }
    }
}
using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Create;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.Scheduling.Create;

public sealed class CreateSchedulingRequestCommandHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new()
    {
        UtcNow = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Handle_WhenStartAtIsInPast_ReturnsInvalidRequestFailure()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            StartAt = _clock.UtcNow.AddMinutes(-5),
        };

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.InvalidRequest);
        result.ErrorMessage.Should().Be("Start time must be in the future.");
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(0);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_SameKeyAndPayload_ReplaysOriginalItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        await SeedSelectablePoiAsync(dbContext, "Dragon Bridge", 16.0615m, 108.2277m);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);
        first.Value.Items.Select(item => item.TravelDurationToNextMinutes)
            .Should().Contain(duration => duration.HasValue);
        replay.Value.Items.Select(item => item.TravelDurationToNextMinutes)
            .Should().Equal(first.Value.Items.Select(item => item.TravelDurationToNextMinutes));
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_InfeasibleEndPoi_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            EndPoiId = 999_999,
            ReturnToStart = false,
        };

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        first.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        first.ErrorMessage.Should().Be("The selected ending location is unavailable.");
        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        replay.ErrorMessage.Should().Be(first.ErrorMessage);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
        var persisted = await dbContext.SchedulingRequests.SingleAsync();
        persisted.Status.Should().Be(SchedulingRequestStatus.Failed);
        persisted.FailureCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_MandatoryPoiOutsideSearchArea_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var outsidePoi = await SeedSelectablePoiAsync(dbContext, "Outside area", 16.3000m, 108.3000m);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [outsidePoi.Id],
        };

        await AssertInfeasibleReplayAsync(handler, command, dbContext);
    }

    [Fact]
    public async Task Handle_NoSelectablePoi_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        await AssertInfeasibleReplayAsync(handler, command, dbContext);
    }

    [Fact]
    public async Task Handle_PublicTransit_ReplaysStoredFailureForSameKey()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            TransportMode = TransportMode.PublicTransit,
        };

        await AssertInfeasibleReplayAsync(handler, command, dbContext);
    }

    [Fact]
    public async Task Handle_SameKeyWithDifferentPayload_ReturnsConflict()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        await handler.Handle(command, CancellationToken.None);
        var mismatch = await handler.Handle(
            command with { AvailableMinutes = 420 },
            CancellationToken.None);

        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be(SchedulingErrorCodes.IdempotencyKeyPayloadMismatch);
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_EquivalentNormalizedCoordinates_ReplaysInsteadOfReturningConflict()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());
        var equivalent = command with
        {
            StartLatitude = 16.0544004m,
            StartLongitude = 108.2022004m,
            ExplorationLatitude = 16.0471004m,
            ExplorationLongitude = 108.2068004m,
            SearchRadiusKm = 10.0004m,
            BudgetVnd = 800_000.0004m,
        };

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(equivalent, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ItineraryId.Should().Be(first.Value.ItineraryId);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_FourthSuccessfulRequestOnSameLocalDate_IsAllowed()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var handler = CreateHandler(dbContext);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await handler.Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
        }

        var fourth = await handler.Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);

        fourth.IsSuccess.Should().BeTrue();
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(4);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    [InlineData(true, "not-json")]
    [InlineData(true, "[\"unknown\"]")]
    public async Task Handle_ProfileWithoutMatchingInterest_PreservesFallbackOrdering(
        bool profileExists,
        string? interestTagsJson)
    {
        await using var dbContext = TestDbContext.Create();
        var (fallbackPoi, _) = await SeedPreferenceRankingPoisAsync(dbContext);
        if (profileExists)
        {
            dbContext.TravelerProfiles.Add(TravelerProfile.Create(
                42,
                interestTagsJson,
                _clock.UtcNow));
            await dbContext.SaveChangesAsync();
        }

        var result = await CreateHandler(dbContext).Handle(
            CreateCommand(Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitPoiIds(result.Value).Should().StartWith(fallbackPoi.Id);
    }

    [Fact]
    public async Task Handle_UsesSavedTravelerInterestTagsToRankOptionalPois()
    {
        await using var dbContext = TestDbContext.Create();
        var (_, preferredPoi) = await SeedPreferenceRankingPoisAsync(dbContext);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"culture\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(
            CreateCommand(Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // This fixture isolates the positive BaseScore contribution from saved InterestTags;
        // it does not assert universal dominance over behavior, quality, AI, or feasibility.
        VisitPoiIds(result.Value).Should().StartWith(preferredPoi.Id);
    }

    [Fact]
    public async Task Handle_SameKeyAndPayload_WhenTravelerProfileAdded_ReplaysOriginalItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        var (fallbackPoi, _) = await SeedPreferenceRankingPoisAsync(dbContext);
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var originalOrder = VisitPoiIds(first.Value);
        originalOrder.Should().StartWith(fallbackPoi.Id);

        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"culture\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();

        var replay = await handler.Handle(command, CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        await AssertSuccessfulReplayAsync(dbContext, first.Value, replay.Value, originalOrder);
    }

    [Fact]
    public async Task Handle_SameKeyAndPayload_WhenTravelerProfileChanged_ReplaysOriginalItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        var (fallbackPoi, _) = await SeedPreferenceRankingPoisAsync(dbContext);
        var profile = TravelerProfile.Create(42, "[\"unknown\"]", _clock.UtcNow);
        dbContext.TravelerProfiles.Add(profile);
        await dbContext.SaveChangesAsync();
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var originalOrder = VisitPoiIds(first.Value);
        originalOrder.Should().StartWith(fallbackPoi.Id);

        dbContext.Entry(profile).Property(item => item.InterestTagsJson).CurrentValue = "[\"culture\"]";
        await dbContext.SaveChangesAsync();

        var replay = await handler.Handle(command, CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        await AssertSuccessfulReplayAsync(dbContext, first.Value, replay.Value, originalOrder);
    }

    [Fact]
    public async Task Handle_SameKeyAndPayload_WhenTravelerProfileRemoved_ReplaysOriginalItinerary()
    {
        await using var dbContext = TestDbContext.Create();
        var (_, preferredPoi) = await SeedPreferenceRankingPoisAsync(dbContext);
        var profile = TravelerProfile.Create(42, "[\"culture\"]", _clock.UtcNow);
        dbContext.TravelerProfiles.Add(profile);
        await dbContext.SaveChangesAsync();
        var handler = CreateHandler(dbContext);
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        var originalOrder = VisitPoiIds(first.Value);
        originalOrder.Should().StartWith(preferredPoi.Id);

        dbContext.TravelerProfiles.Remove(profile);
        await dbContext.SaveChangesAsync();

        var replay = await handler.Handle(command, CancellationToken.None);

        replay.IsSuccess.Should().BeTrue();
        await AssertSuccessfulReplayAsync(dbContext, first.Value, replay.Value, originalOrder);
    }

    [Fact]
    public async Task Handle_MoreThanSixtyEligiblePois_BoundsMatrixAndRetainsMandatoryPois()
    {
        await using var dbContext = TestDbContext.Create();
        var pois = new List<PointOfInterest>();
        for (var index = 0; index < 65; index++)
        {
            pois.Add(await SeedSelectablePoiAsync(
                dbContext,
                $"Candidate {index:D2}",
                16.0471m + (index * 0.0001m),
                108.2068m + (index * 0.0001m)));
        }

        var routeProvider = new RecordingRouteDurationProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [pois[^2].Id, pois[^1].Id],
        };

        var result = await CreateHandler(dbContext, routeProvider).Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        routeProvider.LastPointCount.Should().BeLessOrEqualTo(42);
        result.Value.Items.Select(item => item.PoiId)
            .Should().Contain([pois[^2].Id, pois[^1].Id]);
    }

    [Fact]
    public async Task Handle_ReplayPrecheckHit_SkipsRankingAndUsesAuthoritativeReplay()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedSelectablePoiAsync(dbContext);
        var rankingProvider = new RecordingPoiRankingProvider();
        var handler = CreateHandler(dbContext, rankingProvider: rankingProvider);
        var command = CreateCommand(Guid.NewGuid());

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(
            command with { AvailableMinutes = 420 },
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(SchedulingErrorCodes.IdempotencyKeyPayloadMismatch);
        rankingProvider.CallCount.Should().Be(1);
        dbContext.TransactionExecutionCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_PrecheckMiss_ProjectsOnlyOptionalPoisAndRanksBeforeTransactionAndLock()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        var optionalPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Optional garden",
            16.0472m,
            108.2069m,
            "https://example.com/garden");
        optionalPoi.AddTag(Tag.Create("River Walk"));
        dbContext.PointsOfInterest.Add(optionalPoi);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(optionalPoi).Property(poi => poi.ScenicScore).CurrentValue = 8.5m;
        dbContext.Entry(optionalPoi).Property(poi => poi.PhotoRating).CurrentValue = 7.5m;
        dbContext.Entry(optionalPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue = 75_000m;
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\" NATURE \" , \"river walk\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();

        var schedulingLock = new RecordingSchedulingRequestLock();
        var rankingProvider = new RecordingPoiRankingProvider
        {
            BeforeReturn = () =>
            {
                dbContext.TransactionExecutionCount.Should().Be(0);
                schedulingLock.CallCount.Should().Be(0);
            },
        };
        var handler = CreateHandler(
            dbContext,
            rankingProvider: rankingProvider,
            schedulingRequestLock: schedulingLock);
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(1);
        rankingProvider.LastRequest.Should().NotBeNull();
        rankingProvider.LastRequest!.Context.PreferenceTokens.Should()
            .BeEquivalentTo(["nature", "river walk"]);
        rankingProvider.LastRequest.Candidates.Should().ContainSingle();
        rankingProvider.LastRequest.Candidates.Single().Should().BeEquivalentTo(
            new PoiRankingCandidate(
                optionalPoi.Id,
                "Optional garden",
                "Nature",
                ["River Walk"]));
        rankingProvider.LastRequest.Candidates.Should()
            .NotContain(candidate => candidate.PoiId == mandatoryPoi.Id);
        schedulingLock.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_PrecheckMissWithNoOptionalPois_UsesCurrentMandatorySemantics()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(dbContext);
        var rankingProvider = new RecordingPoiRankingProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var result = await CreateHandler(dbContext, rankingProvider: rankingProvider)
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(0);
        result.Value.Items.Select(item => item.PoiId).Should().Contain(mandatoryPoi.Id);
    }

    [Theory]
    [InlineData("scenic")]
    [InlineData("photo")]
    [InlineData("distance")]
    [InlineData("cost")]
    public async Task Handle_PrecheckMiss_MapsRankingTieBreakerScalars(string scenario)
    {
        await using var dbContext = TestDbContext.Create();
        var preferredPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Preferred candidate",
            16.0472m,
            108.2069m,
            "https://example.com/preferred");
        var otherPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Other candidate",
            scenario == "distance" ? 16.0572m : 16.0473m,
            scenario == "distance" ? 108.2169m : 108.2070m,
            "https://example.com/other");
        dbContext.PointsOfInterest.AddRange(preferredPoi, otherPoi);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(preferredPoi).Property(poi => poi.ScenicScore).CurrentValue =
            scenario == "scenic" ? 9m : 5m;
        dbContext.Entry(otherPoi).Property(poi => poi.ScenicScore).CurrentValue =
            scenario == "scenic" ? 8m : 5m;
        dbContext.Entry(preferredPoi).Property(poi => poi.PhotoRating).CurrentValue =
            scenario == "photo" ? 9m : 5m;
        dbContext.Entry(otherPoi).Property(poi => poi.PhotoRating).CurrentValue =
            scenario == "photo" ? 8m : 5m;
        dbContext.Entry(preferredPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue =
            scenario == "cost" ? 50_000m : 60_000m;
        dbContext.Entry(otherPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue =
            scenario == "cost" ? 70_000m : 60_000m;
        await dbContext.SaveChangesAsync();

        var rankingProvider = new RecordingPoiRankingProvider();
        var result = await CreateHandler(
                dbContext,
                rankingProvider: rankingProvider,
                rankingOptions: TieBreakerOnlyOptions())
            .Handle(CreateCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.LastRequest!.Candidates.Should().ContainSingle();
        rankingProvider.LastRequest.Candidates.Single().PoiId.Should().Be(preferredPoi.Id);
    }

    [Fact]
    public async Task Handle_ProviderPoolBoundary_ExcludesOutsidePoolWithoutBackfillAndRetainsMandatory()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        var pooledPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Pooled garden",
            16.0472m,
            108.2069m,
            "https://example.com/pooled");
        var outsidePoolPois = Enumerable.Range(1, 3)
            .Select(index => CreateSelectablePoi(
                PoiCategory.Create("Culture", null),
                $"Outside pool {index}",
                16.0472m + (index * 0.0001m),
                108.2069m + (index * 0.0001m),
                $"https://example.com/outside-{index}"))
            .ToArray();
        outsidePoolPois[0].AddTag(Tag.Create("Favorite"));
        dbContext.PointsOfInterest.AddRange([pooledPoi, .. outsidePoolPois]);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(pooledPoi).Property(poi => poi.ScenicScore).CurrentValue = 10m;
        for (var index = 0; index < outsidePoolPois.Length; index++)
        {
            dbContext.Entry(outsidePoolPois[index]).Property(poi => poi.ScenicScore).CurrentValue =
                9m - index;
        }

        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"favorite\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();
        var rankingProvider = new RecordingPoiRankingProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 600,
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var result = await CreateHandler(
                dbContext,
                rankingProvider: rankingProvider,
                rankingOptions: TieBreakerOnlyOptions())
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(1);
        rankingProvider.LastRequest!.Candidates.Select(candidate => candidate.PoiId)
            .Should().Equal(pooledPoi.Id);
        VisitPoiIds(result.Value).Should().Contain([mandatoryPoi.Id, pooledPoi.Id]);
        VisitPoiIds(result.Value).Should()
            .NotContain(outsidePoolPois.Select(poi => (long?)poi.Id));
    }

    [Fact]
    public async Task Handle_MatrixCapacityPrefersEffectiveScoreOverLegacyPreferenceScore()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPois = await SeedMandatoryMatrixPoisAsync(dbContext);
        var higherEffectivePoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Higher effective candidate",
            16.0472m,
            108.2069m,
            "https://example.com/higher-effective");
        var higherLegacyPreferencePoi = CreateSelectablePoi(
            PoiCategory.Create("Culture", null),
            "Higher legacy preference candidate",
            16.0473m,
            108.2070m,
            "https://example.com/higher-legacy-preference");
        higherLegacyPreferencePoi.AddTag(Tag.Create("Favorite"));
        dbContext.PointsOfInterest.AddRange(higherEffectivePoi, higherLegacyPreferencePoi);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"favorite\"]",
            _clock.UtcNow));
        await dbContext.SaveChangesAsync();
        dbContext.Entry(higherEffectivePoi).Property(poi => poi.ScenicScore).CurrentValue = 1m;
        dbContext.Entry(higherLegacyPreferencePoi).Property(poi => poi.ScenicScore).CurrentValue = 10m;
        await dbContext.SaveChangesAsync();

        var rankingProvider = new RecordingPoiRankingProvider
        {
            RankedItemsFactory = request => request.Candidates
                .Select(candidate => new PoiRankingItem(
                    candidate.PoiId,
                    candidate.PoiId == higherEffectivePoi.Id ? 0.9m : 0.1m,
                    null))
                .ToArray(),
        };
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 720,
            MandatoryPoiIds = mandatoryPois.Select(poi => poi.Id).ToArray(),
        };

        var result = await CreateHandler(
                dbContext,
                rankingProvider: rankingProvider,
                rankingOptions: MatrixOrderingOptions(),
                generationOptions: new SchedulingGenerationOptions { MaxMatrixCandidates = 6 })
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        VisitPoiIds(result.Value).Should().Contain(higherEffectivePoi.Id);
        VisitPoiIds(result.Value).Should().NotContain(higherLegacyPreferencePoi.Id);
        VisitPoiIds(result.Value).Should().Contain(mandatoryPois.Select(poi => (long?)poi.Id));
    }

    [Theory]
    [InlineData("scenic")]
    [InlineData("scenic-null")]
    [InlineData("photo")]
    [InlineData("photo-null")]
    [InlineData("distance")]
    [InlineData("cost")]
    [InlineData("cost-null")]
    [InlineData("poi-id")]
    public async Task Handle_MatrixCapacityUsesFrozenSnapshotTieBreakers(string scenario)
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPois = await SeedMandatoryMatrixPoisAsync(dbContext);
        var preferredPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Frozen preferred candidate",
            16.0472m,
            108.2069m,
            "https://example.com/frozen-preferred");
        var equalDistance = scenario is "cost" or "cost-null" or "poi-id";
        var otherPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Current preferred candidate",
            scenario == "distance" ? 16.0572m : equalDistance ? 16.0472m : 16.0473m,
            scenario == "distance" ? 108.2169m : equalDistance ? 108.2069m : 108.2070m,
            "https://example.com/current-preferred");
        dbContext.PointsOfInterest.AddRange(preferredPoi, otherPoi);
        await dbContext.SaveChangesAsync();

        dbContext.Entry(preferredPoi).Property(poi => poi.ScenicScore).CurrentValue =
            scenario is "scenic" or "scenic-null" ? 9m : 5m;
        dbContext.Entry(otherPoi).Property(poi => poi.ScenicScore).CurrentValue =
            scenario == "scenic" ? 8m : scenario == "scenic-null" ? null : 5m;
        dbContext.Entry(preferredPoi).Property(poi => poi.PhotoRating).CurrentValue =
            scenario is "photo" or "photo-null" ? 9m : 5m;
        dbContext.Entry(otherPoi).Property(poi => poi.PhotoRating).CurrentValue =
            scenario == "photo" ? 8m : scenario == "photo-null" ? null : 5m;
        dbContext.Entry(preferredPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue =
            scenario is "cost" or "cost-null" ? 50_000m : 60_000m;
        dbContext.Entry(otherPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue =
            scenario == "cost" ? 70_000m : scenario == "cost-null" ? null : 60_000m;
        await dbContext.SaveChangesAsync();

        var rankingProvider = new RecordingPoiRankingProvider
        {
            BeforeReturn = () =>
            {
                switch (scenario)
                {
                    case "scenic":
                    case "scenic-null":
                        dbContext.Entry(preferredPoi).Property(poi => poi.ScenicScore).CurrentValue = null;
                        dbContext.Entry(otherPoi).Property(poi => poi.ScenicScore).CurrentValue = 10m;
                        break;
                    case "photo":
                    case "photo-null":
                        dbContext.Entry(preferredPoi).Property(poi => poi.PhotoRating).CurrentValue = null;
                        dbContext.Entry(otherPoi).Property(poi => poi.PhotoRating).CurrentValue = 10m;
                        break;
                    case "distance":
                        dbContext.Entry(preferredPoi).Property(poi => poi.Latitude).CurrentValue = 16.08m;
                        dbContext.Entry(otherPoi).Property(poi => poi.Latitude).CurrentValue = 16.04715m;
                        break;
                    case "cost":
                    case "cost-null":
                        dbContext.Entry(preferredPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue = null;
                        dbContext.Entry(otherPoi).Property(poi => poi.EstimatedVisitCost).CurrentValue = 10_000m;
                        break;
                    case "poi-id":
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(scenario));
                }

                dbContext.SaveChanges();
            },
        };
        var routeProvider = new RecordingRouteDurationProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 720,
            BudgetVnd = null,
            MandatoryPoiIds = mandatoryPois.Select(poi => poi.Id).ToArray(),
        };

        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider,
                rankingOptions: MatrixOrderingOptions(),
                generationOptions: new SchedulingGenerationOptions { MaxMatrixCandidates = 6 })
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        routeProvider.LastPointCount.Should().Be(8);
        VisitPoiIds(result.Value).Should().Contain(preferredPoi.Id);
        VisitPoiIds(result.Value).Should().NotContain(otherPoi.Id);
    }

    [Fact]
    public async Task Handle_AiDisabledMatrixPreselectionMatchesFrozenBaseTopK()
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPois = await SeedMandatoryMatrixPoisAsync(dbContext);
        var optionals = Enumerable.Range(0, 3)
            .Select(index => CreateSelectablePoi(
                PoiCategory.Create("Nature", null),
                $"Base candidate {index}",
                16.0472m + (index * 0.0001m),
                108.2069m + (index * 0.0001m),
                $"https://example.com/base-{index}"))
            .ToArray();
        dbContext.PointsOfInterest.AddRange(optionals);
        await dbContext.SaveChangesAsync();
        for (var index = 0; index < optionals.Length; index++)
        {
            dbContext.Entry(optionals[index]).Property(poi => poi.ScenicScore).CurrentValue =
                10m - index;
        }

        await dbContext.SaveChangesAsync();
        var rankingProvider = new RecordingPoiRankingProvider();
        var routeProvider = new RecordingRouteDurationProvider();
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 720,
            MandatoryPoiIds = mandatoryPois.Select(poi => poi.Id).ToArray(),
        };

        var result = await CreateHandler(
                dbContext,
                routeProvider,
                rankingProvider,
                rankingOptions: BaseOnlyScenicOptions(),
                generationOptions: new SchedulingGenerationOptions { MaxMatrixCandidates = 6 },
                providerEnabled: false)
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(0);
        routeProvider.LastPoints.Should().Contain(new RoutePoint(
            optionals[0].Latitude,
            optionals[0].Longitude));
        routeProvider.LastPoints.Should().NotContain(new RoutePoint(
            optionals[1].Latitude,
            optionals[1].Longitude));
        routeProvider.LastPoints.Should().NotContain(new RoutePoint(
            optionals[2].Latitude,
            optionals[2].Longitude));
    }

    [Fact]
    public void ToCandidate_OptionalUsesFrozenScoresAndCurrentFeasibilityValues()
    {
        var poi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Current optional",
            16.0472m,
            108.2069m,
            "https://example.com/current-optional");
        poi.AddTag(Tag.Create("Favorite"));
        using var currentContext = TestDbContext.Create();
        currentContext.PointsOfInterest.Add(poi);
        currentContext.SaveChanges();
        currentContext.Entry(poi).Property(item => item.ScenicScore).CurrentValue = 1m;
        currentContext.Entry(poi).Property(item => item.PhotoRating).CurrentValue = 2m;
        currentContext.Entry(poi).Property(item => item.EstimatedVisitCost).CurrentValue = 200_000m;
        var frozenEntry = new PoiRankingSnapshotEntry(
            poi.Id,
            TripMateBaseScore: 0.321m,
            EffectiveDesirabilityScore: 0.654m,
            ScenicScoreForRanking: 9m,
            PhotoRatingForRanking: 8m,
            ExplorationDistanceForRanking: 1.25m,
            EstimatedVisitCostForRanking: 50_000m);

        GenerationCandidate candidate = CreateSchedulingRequestCommandHandler.ToCandidate(
            poi,
            new HashSet<string>(["favorite"]),
            frozenEntry);

        candidate.TripMateBaseScore.Should().Be(0.321m);
        candidate.EffectiveDesirabilityScore.Should().Be(0.654m);
        candidate.ScenicScoreForRanking.Should().Be(9m);
        candidate.PhotoRatingForRanking.Should().Be(8m);
        candidate.EstimatedVisitCostForRanking.Should().Be(50_000m);
        candidate.PreferenceScore.Should().Be(100);
        candidate.ScenicScore.Should().Be(1m);
        candidate.PhotoRating.Should().Be(2m);
        candidate.EstimatedVisitCost.Should().Be(200_000m);
    }

    [Fact]
    public void ToCandidate_MandatoryUsesExplicitNeutralScores()
    {
        var poi = CreateSelectablePoi(
            PoiCategory.Create("Culture", null),
            "Mandatory candidate",
            16.0471m,
            108.2068m,
            "https://example.com/mandatory");

        GenerationCandidate candidate = CreateSchedulingRequestCommandHandler.ToCandidate(
            poi,
            new HashSet<string>(),
            rankingEntry: null);

        candidate.TripMateBaseScore.Should().Be(0m);
        candidate.EffectiveDesirabilityScore.Should().Be(0m);
        candidate.ScenicScoreForRanking.Should().BeNull();
        candidate.PhotoRatingForRanking.Should().BeNull();
        candidate.EstimatedVisitCostForRanking.Should().BeNull();
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("not-planning-ready")]
    [InlineData("outside-radius")]
    [InlineData("missing")]
    public async Task Handle_PooledOptionalInvalidatedInPhaseTwo_DropsWithoutBackfill(
        string invalidation)
    {
        await using var dbContext = TestDbContext.Create();
        var mandatoryPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Mandatory museum",
            16.0471m,
            108.2068m);
        var pooledPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Pooled candidate",
            16.0472m,
            108.2069m);
        var outsidePoolPoi = await SeedSelectablePoiAsync(
            dbContext,
            "Outside-pool backfill candidate",
            16.0473m,
            108.2070m);
        dbContext.Entry(pooledPoi).Property(poi => poi.ScenicScore).CurrentValue = 10m;
        dbContext.Entry(outsidePoolPoi).Property(poi => poi.ScenicScore).CurrentValue = 9m;
        await dbContext.SaveChangesAsync();

        var rankingProvider = new RecordingPoiRankingProvider
        {
            BeforeReturn = () =>
            {
                switch (invalidation)
                {
                    case "inactive":
                        dbContext.Entry(pooledPoi).Property(poi => poi.Status).CurrentValue =
                            PointOfInterestStatus.Inactive;
                        break;
                    case "not-planning-ready":
                        dbContext.Entry(pooledPoi).Property(poi => poi.SourceUrl).CurrentValue = null;
                        break;
                    case "outside-radius":
                        dbContext.Entry(pooledPoi).Property(poi => poi.Latitude).CurrentValue = 17m;
                        break;
                    case "missing":
                        dbContext.PointsOfInterest.Remove(pooledPoi);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(invalidation));
                }

                dbContext.SaveChanges();
            },
        };
        var command = CreateCommand(Guid.NewGuid()) with
        {
            AvailableMinutes = 600,
            MandatoryPoiIds = [mandatoryPoi.Id],
        };

        var result = await CreateHandler(
                dbContext,
                rankingProvider: rankingProvider,
                rankingOptions: TieBreakerOnlyOptions())
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        rankingProvider.CallCount.Should().Be(1);
        VisitPoiIds(result.Value).Should().Contain(mandatoryPoi.Id);
        VisitPoiIds(result.Value).Should().NotContain(pooledPoi.Id);
        VisitPoiIds(result.Value).Should().NotContain(outsidePoolPoi.Id);
    }

    private CreateSchedulingRequestCommandHandler CreateHandler(
        TestDbContext dbContext,
        IRouteDurationProvider? routeDurationProvider = null,
        RecordingPoiRankingProvider? rankingProvider = null,
        ISchedulingRequestLock? schedulingRequestLock = null,
        PersonalizationRankingOptions? rankingOptions = null,
        SchedulingGenerationOptions? generationOptions = null,
        bool providerEnabled = true) =>
        new(
            dbContext,
            _clock,
            routeDurationProvider ?? new FixedRouteDurationProvider(),
            schedulingRequestLock ?? new NoOpSchedulingRequestLock(),
            CreateRankingOrchestrator(
                rankingProvider ?? new RecordingPoiRankingProvider(),
                rankingOptions,
                providerEnabled),
            generationOptions);

    private static PoiRankingOrchestrator CreateRankingOrchestrator(
        IPoiRankingProvider rankingProvider,
        PersonalizationRankingOptions? rankingOptions,
        bool providerEnabled = true)
    {
        var aggregationOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var aggregationContext = new ApplicationDbContext(aggregationOptions);

        return new PoiRankingOrchestrator(
            new PersonalBehaviorFeatureAggregator(aggregationContext),
            rankingProvider,
            rankingOptions ?? new PersonalizationRankingOptions(),
            providerEnabled,
            NullLogger<PoiRankingOrchestrator>.Instance);
    }

    private static PersonalizationRankingOptions TieBreakerOnlyOptions() => new()
    {
        CategoryAffinityWeight = 0m,
        TagAffinityWeight = 0m,
        BehaviorAffinityWeight = 1m,
        ScenicQualityWeight = 0m,
        PhotoQualityWeight = 0m,
        MaxProviderCandidates = 1,
    };

    private static PersonalizationRankingOptions MatrixOrderingOptions() => new()
    {
        BaseWeight = 0m,
        AiWeight = 1m,
        MaxProviderCandidates = 2,
    };

    private static PersonalizationRankingOptions BaseOnlyScenicOptions() => new()
    {
        CategoryAffinityWeight = 0m,
        TagAffinityWeight = 0m,
        BehaviorAffinityWeight = 0m,
        ScenicQualityWeight = 1m,
        PhotoQualityWeight = 0m,
        MaxProviderCandidates = 2,
    };

    private static async Task<IReadOnlyList<PointOfInterest>> SeedMandatoryMatrixPoisAsync(
        TestDbContext dbContext)
    {
        var mandatoryPois = new List<PointOfInterest>();
        for (var index = 0; index < 5; index++)
        {
            mandatoryPois.Add(await SeedSelectablePoiAsync(
                dbContext,
                $"Mandatory {index}",
                16.0471m + (index * 0.00001m),
                108.2068m + (index * 0.00001m)));
        }

        return mandatoryPois;
    }

    private static Task<PointOfInterest> SeedSelectablePoiAsync(TestDbContext dbContext) =>
        SeedSelectablePoiAsync(dbContext, "Cham Museum", 16.0471m, 108.2068m);

    private static async Task<PointOfInterest> SeedSelectablePoiAsync(
        TestDbContext dbContext,
        string name,
        decimal latitude,
        decimal longitude)
    {
        var category = PoiCategory.Create("Culture", null);
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, "https://example.com/cham", DateTimeOffset.UtcNow);
        poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();
        return poi;
    }

    private async Task<(PointOfInterest FallbackPoi, PointOfInterest PreferredPoi)>
        SeedPreferenceRankingPoisAsync(TestDbContext dbContext)
    {
        var fallbackPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "Fallback attraction",
            16.0471m,
            108.2068m,
            "https://example.com/fallback");
        var preferredPoi = CreateSelectablePoi(
            PoiCategory.Create("Culture", null),
            "Preferred museum",
            16.0472m,
            108.2069m,
            "https://example.com/preferred");

        dbContext.PointsOfInterest.AddRange(fallbackPoi, preferredPoi);
        await dbContext.SaveChangesAsync();
        return (fallbackPoi, preferredPoi);
    }

    private PointOfInterest CreateSelectablePoi(
        PoiCategory category,
        string name,
        decimal latitude,
        decimal longitude,
        string sourceUrl)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            1,
            _clock.UtcNow,
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, sourceUrl, _clock.UtcNow);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            2,
            new TimeOnly(7, 0),
            new TimeOnly(20, 0),
            false));
        return poi;
    }

    private static long?[] VisitPoiIds(SchedulingResponseDto response) =>
        response.Items
            .Where(item => item.ItemKind == ItineraryItemKind.Visit)
            .Select(item => item.PoiId)
            .ToArray();

    private static async Task AssertSuccessfulReplayAsync(
        TestDbContext dbContext,
        SchedulingResponseDto first,
        SchedulingResponseDto replay,
        IReadOnlyCollection<long?> originalOrder)
    {
        replay.ItineraryId.Should().Be(first.ItineraryId);
        VisitPoiIds(replay).Should().Equal(originalOrder);
        (await dbContext.Itineraries.CountAsync()).Should().Be(1);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
    }

    private static CreateSchedulingRequestCommand CreateCommand(Guid key) => new(
        TravelerUserId: 42,
        IdempotencyKey: key,
        StartAt: new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(7)),
        TimeZoneId: "Asia/Ho_Chi_Minh",
        StartLatitude: 16.0544m,
        StartLongitude: 108.2022m,
        ExplorationLatitude: 16.0471m,
        ExplorationLongitude: 108.2068m,
        EndPoiId: null,
        ReturnToStart: true,
        AvailableMinutes: 480,
        TransportMode: TransportMode.Motorbike,
        SearchRadiusKm: 10m,
        BudgetVnd: 800_000m,
        MandatoryPoiIds: [],
        RestPreference: RestPreference.None);

    private static async Task AssertInfeasibleReplayAsync(
        CreateSchedulingRequestCommandHandler handler,
        CreateSchedulingRequestCommand command,
        TestDbContext dbContext)
    {
        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsFailure.Should().BeTrue();
        first.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        replay.IsFailure.Should().BeTrue();
        replay.ErrorCode.Should().Be(SchedulingErrorCodes.ConstraintsInfeasible);
        (await dbContext.SchedulingRequests.CountAsync()).Should().Be(1);
        (await dbContext.SchedulingRequests.SingleAsync()).Status.Should().Be(SchedulingRequestStatus.Failed);
        (await dbContext.Itineraries.CountAsync()).Should().Be(0);
    }

    private sealed class FixedRouteDurationProvider : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            var durations = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    durations[row, column] = row == column ? 0 : 15;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations));
        }
    }

    private sealed class RecordingRouteDurationProvider : IRouteDurationProvider
    {
        public int LastPointCount { get; private set; }

        public IReadOnlyList<RoutePoint> LastPoints { get; private set; } = [];

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            LastPointCount = points.Count;
            LastPoints = points.ToArray();
            var durations = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    durations[row, column] = row == column ? 0 : 15;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations));
        }
    }

    private sealed class NoOpSchedulingRequestLock : ISchedulingRequestLock
    {
        public Task AcquireAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingSchedulingRequestLock : ISchedulingRequestLock
    {
        public int CallCount { get; private set; }

        public Task AcquireAsync(
            long travelerUserId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPoiRankingProvider : IPoiRankingProvider
    {
        public Action? BeforeReturn { get; init; }

        public Func<PoiRankingRequest, IReadOnlyCollection<PoiRankingItem>>? RankedItemsFactory
        {
            get;
            init;
        }

        public int CallCount { get; private set; }

        public PoiRankingRequest? LastRequest { get; private set; }

        public Task<Result<PoiRankingResult>> RankAsync(
            PoiRankingRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            BeforeReturn?.Invoke();
            return Task.FromResult(Result.Success(new PoiRankingResult(
                RankedItemsFactory?.Invoke(request)
                ?? request.Candidates
                    .Select(candidate => new PoiRankingItem(candidate.PoiId, 0.5m, null))
                    .ToArray())));
        }
    }
}
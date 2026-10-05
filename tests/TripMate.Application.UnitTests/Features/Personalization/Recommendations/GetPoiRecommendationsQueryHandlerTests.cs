using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Features.Personalization.Recommendations;
using TripMate.Application.Features.PointsOfInterest.Search;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.Personalization.Recommendations;

public sealed class GetPoiRecommendationsQueryHandlerTests
{
    private static readonly DateTimeOffset SeedTime =
        new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WithNoEligibleCandidates_ReturnsSuccessfulEmptyResult()
    {
        await using var dbContext = CreateDbContext();

        var result = await CreateHandler(dbContext).Handle(
            CreateQuery(),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAvailable.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ReturnsOnlyActivePlanningReadyPoisInsideExactRadius()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();

        var eligible = CreatePlanningReadyPoi(category, "Eligible", 16.0471m, 108.2068m);
        var outsideRadius = CreatePlanningReadyPoi(category, "Outside", 16.2471m, 108.2068m);
        var inactive = CreatePlanningReadyPoi(category, "Inactive", 16.0471m, 108.2068m);
        var missingSource = CreatePlanningReadyPoi(category, "Missing source", 16.0471m, 108.2068m);
        var missingVerification = CreatePlanningReadyPoi(
            category,
            "Missing verification",
            16.0471m,
            108.2068m);
        var noUsableWindow = PointOfInterest.Create(
            category,
            "No usable window",
            16.0471m,
            108.2068m,
            1,
            SeedTime);
        noUsableWindow.ConfigurePlanningMetadata(
            0m,
            "https://example.com/no-window",
            SeedTime);
        noUsableWindow.AddOpeningHour(PoiOpeningHour.Create(1, null, null, true));
        dbContext.PointsOfInterest.AddRange(
            eligible,
            outsideRadius,
            inactive,
            missingSource,
            missingVerification,
            noUsableWindow);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(inactive).Property(poi => poi.Status).CurrentValue =
            PointOfInterestStatus.Inactive;
        dbContext.Entry(missingSource).Property(poi => poi.SourceUrl).CurrentValue = null;
        dbContext.Entry(missingVerification).Property(poi => poi.VerifiedAtUtc).CurrentValue = null;
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(
            CreateQuery(),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAvailable.Should().Be(1);
        result.Value.Items.Should().ContainSingle().Which.Name.Should().Be("Eligible");
    }

    [Fact]
    public async Task Handle_UsesSameSpatialEligibilityAsSelectablePoiSearch()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        var center = CreatePlanningReadyPoi(category, "Center", 16.0471m, 108.2068m);
        var nearby = CreatePlanningReadyPoi(category, "Nearby", 16.0571m, 108.2068m);
        var outside = CreatePlanningReadyPoi(category, "Outside", 16.2471m, 108.2068m);
        dbContext.PointsOfInterest.AddRange(center, nearby, outside);
        await dbContext.SaveChangesAsync();

        var recommendationResult = await CreateHandler(dbContext).Handle(
            CreateQuery(limit: 20),
            CancellationToken.None);
        var searchResult = await new SearchSelectablePoisQueryHandler(dbContext).Handle(
            new SearchSelectablePoisQuery(
                Query: null,
                Latitude: 16.0471m,
                Longitude: 108.2068m,
                RadiusKm: 10,
                Page: 1,
                PageSize: 20),
            CancellationToken.None);

        recommendationResult.Value.Items.Select(item => item.PoiId)
            .Should().BeEquivalentTo(searchResult.Value.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Handle_WithoutPreferencesOrBehavior_ReturnsDeterministicColdStartOrder()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        var first = CreatePlanningReadyPoi(category, "First", 16.0471m, 108.2068m);
        var second = CreatePlanningReadyPoi(category, "Second", 16.0471m, 108.2068m);
        var third = CreatePlanningReadyPoi(category, "Third", 16.0471m, 108.2068m);
        dbContext.PointsOfInterest.AddRange(first, second, third);
        await dbContext.SaveChangesAsync();

        var handler = CreateHandler(dbContext);
        var firstResult = await handler.Handle(CreateQuery(), CancellationToken.None);
        var secondResult = await handler.Handle(CreateQuery(), CancellationToken.None);

        firstResult.Value.Items.Select(item => item.PoiId)
            .Should().Equal(first.Id, second.Id, third.Id);
        secondResult.Value.Items.Select(item => item.PoiId)
            .Should().Equal(firstResult.Value.Items.Select(item => item.PoiId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handle_NullQualitySignal_UsesNeutralQuality(bool nullScenicScore)
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        var neutral = CreatePlanningReadyPoi(category, "Neutral", 16.0471m, 108.2068m);
        var belowNeutral = CreatePlanningReadyPoi(
            category,
            "Below neutral",
            16.0471m,
            108.2068m);
        dbContext.PointsOfInterest.AddRange(neutral, belowNeutral);
        await dbContext.SaveChangesAsync();
        SetQuality(
            dbContext,
            neutral,
            scenicScore: nullScenicScore ? null : 4m,
            photoRating: nullScenicScore ? 4m : null);
        SetQuality(dbContext, belowNeutral, scenicScore: 4m, photoRating: 4m);
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(
            CreateQuery(),
            CancellationToken.None);

        result.Value.Items.First().PoiId.Should().Be(neutral.Id);
    }

    [Fact]
    public async Task Handle_WithNullLimit_DefaultsToTenAndCountsAllEligibleCandidates()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        dbContext.PointsOfInterest.AddRange(Enumerable.Range(1, 12).Select(index =>
            CreatePlanningReadyPoi(category, $"POI {index}", 16.0471m, 108.2068m)));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(
            CreateQuery(limit: null),
            CancellationToken.None);

        result.Value.TotalAvailable.Should().Be(12);
        result.Value.Items.Should().HaveCount(10);
    }

    [Fact]
    public async Task Handle_WithExplicitLimit_ReturnsRequestedTopCountBeforeTotalAvailable()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        dbContext.PointsOfInterest.AddRange(Enumerable.Range(1, 5).Select(index =>
            CreatePlanningReadyPoi(category, $"POI {index}", 16.0471m, 108.2068m)));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(
            CreateQuery(limit: 2),
            CancellationToken.None);

        result.Value.TotalAvailable.Should().Be(5);
        result.Value.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_CategoryPreferencePromotesMatchingCandidate()
    {
        await using var dbContext = CreateDbContext();
        var otherCategory = PoiCategory.Create("Nature", null);
        var preferredCategory = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.AddRange(otherCategory, preferredCategory);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(42, "[\"culture\"]", SeedTime));
        await dbContext.SaveChangesAsync();
        var other = CreatePlanningReadyPoi(otherCategory, "Other", 16.0471m, 108.2068m);
        var preferred = CreatePlanningReadyPoi(
            preferredCategory,
            "Preferred",
            16.0471m,
            108.2068m);
        dbContext.PointsOfInterest.AddRange(other, preferred);
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(CreateQuery(), CancellationToken.None);

        result.Value.Items.First().PoiId.Should().Be(preferred.Id);
    }

    [Fact]
    public async Task Handle_TagPreferencePromotesMatchingCandidate()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        var museum = Tag.Create("museum");
        var heritage = Tag.Create("heritage");
        var history = Tag.Create("history");
        dbContext.PoiCategories.Add(category);
        dbContext.Tags.AddRange(museum, heritage, history);
        dbContext.TravelerProfiles.Add(TravelerProfile.Create(
            42,
            "[\"museum\",\"heritage\",\"history\"]",
            SeedTime));
        await dbContext.SaveChangesAsync();
        var other = CreatePlanningReadyPoi(category, "Other", 16.0471m, 108.2068m);
        var preferred = CreatePlanningReadyPoi(category, "Preferred", 16.0471m, 108.2068m);
        preferred.AddTag(museum);
        preferred.AddTag(heritage);
        preferred.AddTag(history);
        dbContext.PointsOfInterest.AddRange(other, preferred);
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(CreateQuery(), CancellationToken.None);

        result.Value.Items.First().PoiId.Should().Be(preferred.Id);
    }

    [Theory]
    [InlineData(RecommendationEventType.Like, true)]
    [InlineData(RecommendationEventType.Dislike, false)]
    [InlineData(RecommendationEventType.Skip, false)]
    public async Task Handle_DirectBehaviorAdjustsCandidateRanking(
        RecommendationEventType eventType,
        bool affectedCandidateRanksFirst)
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        var unaffected = CreatePlanningReadyPoi(category, "Unaffected", 16.0471m, 108.2068m);
        var affected = CreatePlanningReadyPoi(category, "Affected", 16.0471m, 108.2068m);
        dbContext.PointsOfInterest.AddRange(unaffected, affected);
        await dbContext.SaveChangesAsync();
        dbContext.RecommendationBehaviorEvents.Add(CreateBehaviorEvent(
            travelerUserId: 42,
            affected.Id,
            eventType));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(CreateQuery(), CancellationToken.None);

        result.Value.Items.First().PoiId.Should().Be(
            affectedCandidateRanksFirst ? affected.Id : unaffected.Id);
    }

    [Fact]
    public async Task Handle_CategoryBehaviorFallbackUsesExistingTwoPoiThreshold()
    {
        await using var dbContext = CreateDbContext();
        var preferredCategory = PoiCategory.Create("Culture", null);
        var otherCategory = PoiCategory.Create("Nature", null);
        dbContext.PoiCategories.AddRange(preferredCategory, otherCategory);
        await dbContext.SaveChangesAsync();
        var preferred = CreatePlanningReadyPoi(
            preferredCategory,
            "Preferred",
            16.0471m,
            108.2068m);
        var other = CreatePlanningReadyPoi(otherCategory, "Other", 16.0471m, 108.2068m);
        var historyOne = PointOfInterest.Create(
            preferredCategory,
            "History one",
            16.0471m,
            108.2068m,
            1,
            SeedTime);
        var historyTwo = PointOfInterest.Create(
            preferredCategory,
            "History two",
            16.0471m,
            108.2068m,
            1,
            SeedTime);
        dbContext.PointsOfInterest.AddRange(other, preferred, historyOne, historyTwo);
        await dbContext.SaveChangesAsync();
        dbContext.RecommendationBehaviorEvents.AddRange(
            CreateBehaviorEvent(42, historyOne.Id, RecommendationEventType.Like),
            CreateBehaviorEvent(42, historyTwo.Id, RecommendationEventType.Like));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(CreateQuery(), CancellationToken.None);

        result.Value.Items.First().PoiId.Should().Be(preferred.Id);
    }

    [Fact]
    public async Task Handle_OtherTravelerBehaviorHasZeroInfluence()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        var first = CreatePlanningReadyPoi(category, "First", 16.0471m, 108.2068m);
        var second = CreatePlanningReadyPoi(category, "Second", 16.0471m, 108.2068m);
        dbContext.PointsOfInterest.AddRange(first, second);
        await dbContext.SaveChangesAsync();
        dbContext.RecommendationBehaviorEvents.Add(CreateBehaviorEvent(
            travelerUserId: 99,
            second.Id,
            RecommendationEventType.Like));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(CreateQuery(), CancellationToken.None);

        result.Value.Items.Select(item => item.PoiId).Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Handle_AppliesFrozenSixLevelOrderingWithNullsLast()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        var scenic = CreatePlanningReadyPoi(category, "Scenic", 16.0871m, 108.2068m, 100m);
        var photo = CreatePlanningReadyPoi(category, "Photo", 16.0771m, 108.2068m, 100m);
        var distance = CreatePlanningReadyPoi(category, "Distance", 16.0571m, 108.2068m, 100m);
        var cost = CreatePlanningReadyPoi(category, "Cost", 16.0671m, 108.2068m, 1m);
        var lowerId = CreatePlanningReadyPoi(category, "Lower ID", 16.0671m, 108.2068m, 2m);
        var higherId = CreatePlanningReadyPoi(category, "Higher ID", 16.0671m, 108.2068m, 2m);
        var nullCost = CreatePlanningReadyPoi(category, "Null cost", 16.0671m, 108.2068m, null);
        var nullPhoto = CreatePlanningReadyPoi(category, "Null photo", 16.0671m, 108.2068m, 0m);
        var nullScenic = CreatePlanningReadyPoi(category, "Null scenic", 16.0471m, 108.2068m, 0m);
        dbContext.PointsOfInterest.AddRange(
            scenic,
            photo,
            distance,
            cost,
            lowerId,
            higherId,
            nullCost,
            nullPhoto,
            nullScenic);
        await dbContext.SaveChangesAsync();
        SetQuality(dbContext, scenic, 9m, 1m);
        SetQuality(dbContext, photo, 8m, 9m);
        SetQuality(dbContext, distance, 8m, 8m);
        SetQuality(dbContext, cost, 8m, 8m);
        SetQuality(dbContext, lowerId, 8m, 8m);
        SetQuality(dbContext, higherId, 8m, 8m);
        SetQuality(dbContext, nullCost, 8m, 8m);
        SetQuality(dbContext, nullPhoto, 8m, null);
        SetQuality(dbContext, nullScenic, null, 10m);
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext, TieOnlyOptions()).Handle(
            CreateQuery(limit: 20),
            CancellationToken.None);

        result.Value.Items.Select(item => item.PoiId).Should().Equal(
            scenic.Id,
            photo.Id,
            distance.Id,
            cost.Id,
            lowerId.Id,
            higherId.Id,
            nullCost.Id,
            nullPhoto.Id,
            nullScenic.Id);
    }

    [Fact]
    public async Task Handle_UsesDeterministicThumbnailAndPublicPoiReviewAverageForDisplayOnly()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        var qualityLeader = CreatePlanningReadyPoi(
            category,
            "Quality leader",
            16.0471m,
            108.2068m);
        var reviewed = CreatePlanningReadyPoi(category, "Reviewed", 16.0471m, 108.2068m);
        dbContext.PointsOfInterest.AddRange(qualityLeader, reviewed);
        await dbContext.SaveChangesAsync();
        SetQuality(dbContext, qualityLeader, 9m, 9m);
        SetQuality(dbContext, reviewed, 1m, 1m);
        dbContext.PoiPhotos.AddRange(
            PoiPhoto.Create(qualityLeader, "https://example.com/sort-2.jpg", sortOrder: 2),
            PoiPhoto.Create(qualityLeader, "https://example.com/primary.jpg", sortOrder: 1),
            PoiPhoto.Create(qualityLeader, "https://example.com/same-sort-later.jpg", sortOrder: 1));
        dbContext.Reviews.AddRange(
            Review.CreatePoiReview(1, reviewed.Id, 4, SeedTime),
            Review.CreatePoiReview(2, reviewed.Id, 4, SeedTime),
            Review.CreatePoiReview(3, reviewed.Id, 5, SeedTime),
            Review.Create(4, Review.TargetTypeTour, reviewed.Id, 5, SeedTime));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(CreateQuery(), CancellationToken.None);

        result.Value.Items.Select(item => item.PoiId)
            .Should().Equal(qualityLeader.Id, reviewed.Id);
        var qualityItem = result.Value.Items.Single(item => item.PoiId == qualityLeader.Id);
        qualityItem.ThumbnailUrl.Should().Be("https://example.com/primary.jpg");
        qualityItem.AverageRating.Should().BeNull();
        result.Value.Items.Single(item => item.PoiId == reviewed.Id)
            .AverageRating.Should().Be(4.3m);
    }

    [Theory]
    [InlineData(
        "category-tag-behavior",
        "Phù hợp với sở thích Văn hóa, các thẻ quan tâm và lịch sử của bạn")]
    [InlineData(
        "category-behavior",
        "Phù hợp với sở thích Văn hóa và lịch sử tương tác của bạn")]
    [InlineData(
        "category-tag",
        "Phù hợp với danh mục Văn hóa và các thẻ sở thích của bạn")]
    [InlineData("category", "Phù hợp với sở thích Văn hóa của bạn")]
    [InlineData("tag", "Phù hợp với các thẻ quan tâm của bạn")]
    [InlineData("behavior", "Phù hợp với lịch sử tương tác của bạn")]
    [InlineData("quality", "Địa điểm được đánh giá cao tại khu vực tìm kiếm")]
    [InlineData("fallback", null)]
    public async Task Handle_GeneratesRecommendationReasonByFrozenPrecedence(
        string scenario,
        string? expectedReason)
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        var tag = Tag.Create("museum");
        dbContext.PoiCategories.Add(category);
        dbContext.Tags.Add(tag);
        var preferences = scenario switch
        {
            "category-tag-behavior" or "category-tag" => "[\"culture\",\"museum\"]",
            "category-behavior" or "category" => "[\"culture\"]",
            "tag" => "[\"museum\"]",
            _ => null,
        };
        if (preferences is not null)
        {
            dbContext.TravelerProfiles.Add(TravelerProfile.Create(42, preferences, SeedTime));
        }
        await dbContext.SaveChangesAsync();
        var poi = CreatePlanningReadyPoi(category, "Candidate", 16.0471m, 108.2068m);
        if (scenario is "category-tag-behavior" or "category-tag" or "tag")
        {
            poi.AddTag(tag);
        }
        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();
        if (scenario is "category-tag-behavior" or "category-behavior" or "behavior")
        {
            dbContext.RecommendationBehaviorEvents.Add(CreateBehaviorEvent(
                42,
                poi.Id,
                RecommendationEventType.Like));
        }
        if (scenario == "quality")
        {
            SetQuality(dbContext, poi, scenicScore: 8m, photoRating: null);
        }
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(CreateQuery(), CancellationToken.None);

        result.Value.Items.Should().ContainSingle().Which.RecommendationReason
            .Should().Be(expectedReason);
    }

    [Fact]
    public async Task Handle_SkipIsAWeakerNegativeSignalThanDislike()
    {
        await using var dbContext = CreateDbContext();
        var neutralCategory = PoiCategory.Create("Neutral", null);
        var skipCategory = PoiCategory.Create("Skip", null);
        var dislikeCategory = PoiCategory.Create("Dislike", null);
        dbContext.PoiCategories.AddRange(neutralCategory, skipCategory, dislikeCategory);
        await dbContext.SaveChangesAsync();
        var neutral = CreatePlanningReadyPoi(
            neutralCategory,
            "Neutral",
            16.0471m,
            108.2068m);
        var skipped = CreatePlanningReadyPoi(
            skipCategory,
            "Skipped",
            16.0471m,
            108.2068m);
        var disliked = CreatePlanningReadyPoi(
            dislikeCategory,
            "Disliked",
            16.0471m,
            108.2068m);
        dbContext.PointsOfInterest.AddRange(neutral, skipped, disliked);
        await dbContext.SaveChangesAsync();
        dbContext.RecommendationBehaviorEvents.AddRange(
            CreateBehaviorEvent(42, skipped.Id, RecommendationEventType.Skip),
            CreateBehaviorEvent(42, disliked.Id, RecommendationEventType.Dislike));
        await dbContext.SaveChangesAsync();

        var result = await CreateHandler(dbContext).Handle(CreateQuery(), CancellationToken.None);

        result.Value.Items.Select(item => item.PoiId)
            .Should().Equal(neutral.Id, skipped.Id, disliked.Id);
    }

    [Fact]
    public void Handler_HasNoAiProviderOrSchedulerDependency()
    {
        var dependencyTypes = typeof(GetPoiRecommendationsQueryHandler)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        dependencyTypes.Should().Equal(
            typeof(TripMate.Application.Common.Interfaces.IApplicationDbContext),
            typeof(PersonalBehaviorFeatureAggregator),
            typeof(IOptions<PersonalizationRankingOptions>));
    }

    [Fact]
    public async Task Handle_PerformsReadOnlyExecutionWithoutTrackedMutations()
    {
        await using var dbContext = CreateDbContext();
        var category = PoiCategory.Create("Culture", null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        dbContext.PointsOfInterest.Add(CreatePlanningReadyPoi(
            category,
            "Candidate",
            16.0471m,
            108.2068m));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var result = await CreateHandler(dbContext).Handle(CreateQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        dbContext.ChangeTracker.Entries().Should().BeEmpty();
    }

    private static GetPoiRecommendationsQuery CreateQuery(int? limit = null) =>
        new(
            TravelerUserId: 42,
            ExplorationLatitude: 16.0471m,
            ExplorationLongitude: 108.2068m,
            SearchRadiusKm: 10,
            Limit: limit);

    private static GetPoiRecommendationsQueryHandler CreateHandler(
        ApplicationDbContext dbContext,
        PersonalizationRankingOptions? rankingOptions = null) =>
        new(
            dbContext,
            new PersonalBehaviorFeatureAggregator(dbContext),
            Options.Create(rankingOptions ?? new PersonalizationRankingOptions()));

    private static PersonalizationRankingOptions TieOnlyOptions() =>
        new()
        {
            CategoryAffinityWeight = 0m,
            TagAffinityWeight = 0m,
            BehaviorAffinityWeight = 1m,
            ScenicQualityWeight = 0m,
            PhotoQualityWeight = 0m,
        };

    private static PointOfInterest CreatePlanningReadyPoi(
        PoiCategory category,
        string name,
        decimal latitude,
        decimal longitude,
        decimal? estimatedVisitCost = 0m)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            1,
            SeedTime);
        poi.ConfigurePlanningMetadata(
            estimatedVisitCost,
            $"https://example.com/{Guid.NewGuid():N}",
            SeedTime);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            1,
            new TimeOnly(8, 0),
            new TimeOnly(17, 0),
            false));
        return poi;
    }

    private static void SetQuality(
        ApplicationDbContext dbContext,
        PointOfInterest poi,
        decimal? scenicScore,
        decimal? photoRating)
    {
        dbContext.Entry(poi).Property(candidate => candidate.ScenicScore).CurrentValue =
            scenicScore;
        dbContext.Entry(poi).Property(candidate => candidate.PhotoRating).CurrentValue =
            photoRating;
    }

    private static RecommendationBehaviorEvent CreateBehaviorEvent(
        long travelerUserId,
        long poiId,
        RecommendationEventType eventType) =>
        RecommendationBehaviorEvent.Create(
            travelerUserId,
            poiId,
            itineraryId: eventType is RecommendationEventType.Skip ? 500 : null,
            eventType,
            originalPosition: eventType is RecommendationEventType.Skip ? 1 : null,
            newPosition: null,
            wasMandatory: eventType is RecommendationEventType.Skip ? false : null,
            source: eventType is RecommendationEventType.Skip
                ? RecommendationCaptureSource.Itinerary
                : RecommendationCaptureSource.Explore,
            SeedTime,
            Guid.NewGuid());

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }
}
using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Personalization.Recommendations;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Personalization;

public sealed class PoiRecommendationsSqlServerTests
{
    private static readonly DateTimeOffset SeedTime =
        new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_PopulatedPath_TranslatesAndExecutesExactlyFiveReadCommands()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var interceptor = new TestCommandCounterInterceptor();
        await using var context = database.CreateDbContext(interceptor);
        var handler = new GetPoiRecommendationsQueryHandler(
            context,
            new PersonalBehaviorFeatureAggregator(context),
            Options.Create(new PersonalizationRankingOptions()));

        var result = await handler.Handle(
            new GetPoiRecommendationsQuery(
                seed.TravelerUserId,
                16.0471m,
                108.2068m,
                SearchRadiusKm: 10,
                Limit: 10),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAvailable.Should().Be(1);
        var item = result.Value.Items.Should().ContainSingle().Subject;
        item.PoiId.Should().Be(seed.PoiId);
        item.ThumbnailUrl.Should().Be("https://example.com/primary.jpg");
        item.AverageRating.Should().Be(4.3m);
        interceptor.CommandCount.Should().Be(5);
        interceptor.CommandTexts.Should().OnlyContain(command =>
            command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    private static async Task<RecommendationSeed> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var traveler = CreateTraveler("recommendation-traveler");
        var reviewerTwo = CreateTraveler("recommendation-reviewer-two");
        var reviewerThree = CreateTraveler("recommendation-reviewer-three");
        var category = PoiCategory.Create("Culture", null);
        var tag = Tag.Create("museum");
        context.Users.AddRange(traveler, reviewerTwo, reviewerThree);
        context.PoiCategories.Add(category);
        context.Tags.Add(tag);
        await context.SaveChangesAsync();

        context.TravelerProfiles.Add(TravelerProfile.Create(
            traveler.Id,
            "[\"culture\",\"museum\"]",
            SeedTime));
        var poi = PointOfInterest.Create(
            category,
            "SQL Recommendation POI",
            16.0471m,
            108.2068m,
            traveler.Id,
            SeedTime);
        poi.ConfigurePlanningMetadata(
            60_000m,
            "https://example.com/source",
            SeedTime);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            1,
            new TimeOnly(8, 0),
            new TimeOnly(17, 0),
            false));
        poi.AddTag(tag);
        context.PointsOfInterest.Add(poi);
        await context.SaveChangesAsync();

        context.PoiPhotos.AddRange(
            PoiPhoto.Create(poi, "https://example.com/later.jpg", sortOrder: 2),
            PoiPhoto.Create(poi, "https://example.com/primary.jpg", sortOrder: 1));
        context.Reviews.AddRange(
            Review.CreatePoiReview(traveler.Id, poi.Id, 4, SeedTime),
            Review.CreatePoiReview(reviewerTwo.Id, poi.Id, 4, SeedTime),
            Review.CreatePoiReview(reviewerThree.Id, poi.Id, 5, SeedTime));
        context.RecommendationBehaviorEvents.Add(RecommendationBehaviorEvent.Create(
            traveler.Id,
            poi.Id,
            itineraryId: null,
            RecommendationEventType.Like,
            originalPosition: null,
            newPosition: null,
            wasMandatory: null,
            RecommendationCaptureSource.Explore,
            SeedTime,
            Guid.NewGuid()));
        await context.SaveChangesAsync();

        return new RecommendationSeed(traveler.Id, poi.Id);
    }

    private static User CreateTraveler(string name) => new()
    {
        Email = $"{name}-{Guid.NewGuid():N}@example.test",
        FullName = name,
        Role = UserRole.Traveler,
        Status = AccountStatus.Active,
        CreatedAtUtc = SeedTime,
        UpdatedAtUtc = SeedTime,
    };

    private sealed record RecommendationSeed(long TravelerUserId, long PoiId);
}
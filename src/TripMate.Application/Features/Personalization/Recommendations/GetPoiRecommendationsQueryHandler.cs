using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Geo;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Personalization.Recommendations;

public sealed class GetPoiRecommendationsQueryHandler(
    IApplicationDbContext dbContext,
    PersonalBehaviorFeatureAggregator behaviorAggregator,
    IOptions<PersonalizationRankingOptions> options)
    : IRequestHandler<GetPoiRecommendationsQuery, Result<PoiRecommendationResultDto>>
{
    private readonly IApplicationDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly PersonalBehaviorFeatureAggregator _behaviorAggregator =
        behaviorAggregator ?? throw new ArgumentNullException(nameof(behaviorAggregator));
    private readonly PersonalizationRankingOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<Result<PoiRecommendationResultDto>> Handle(
        GetPoiRecommendationsQuery query,
        CancellationToken cancellationToken)
    {
        var interestTagsJson = await _dbContext.TravelerProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == query.TravelerUserId)
            .Select(profile => profile.InterestTagsJson)
            .SingleOrDefaultAsync(cancellationToken);
        var preferenceTokens = TravelerPreferenceScoring.ParsePreferenceTokens(interestTagsJson);

        var bounds = LocationBounds.From(
            query.ExplorationLatitude,
            query.ExplorationLongitude,
            query.SearchRadiusKm);
        var candidateRows = await _dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(poi =>
                poi.Status == PointOfInterestStatus.Active
                && poi.SourceUrl != null
                && poi.VerifiedAtUtc != null
                && poi.OpeningHours.Any(hours =>
                    !hours.IsClosed
                    && hours.OpenTime.HasValue
                    && hours.CloseTime.HasValue)
                && poi.Latitude >= bounds.MinimumLatitude
                && poi.Latitude <= bounds.MaximumLatitude
                && poi.Longitude >= bounds.MinimumLongitude
                && poi.Longitude <= bounds.MaximumLongitude)
            .Select(poi => new CandidateRow(
                poi.Id,
                poi.CategoryId,
                poi.Name,
                poi.Category.Name,
                poi.PoiTags.Select(mapping => mapping.Tag.Name).ToArray(),
                poi.Latitude,
                poi.Longitude,
                poi.ScenicScore,
                poi.PhotoRating,
                poi.EstimatedVisitCost))
            .ToArrayAsync(cancellationToken);
        var candidates = candidateRows
            .Select(candidate => new Candidate(
                candidate,
                GeoDistance.EquirectangularKilometers(
                    query.ExplorationLatitude,
                    query.ExplorationLongitude,
                    candidate.Latitude,
                    candidate.Longitude)))
            .Where(candidate => candidate.DistanceKm <= query.SearchRadiusKm)
            .ToArray();

        if (candidates.Length == 0)
        {
            return Result.Success(new PoiRecommendationResultDto([], 0));
        }

        var aggregation = await _behaviorAggregator.AggregateAsync(
            query.TravelerUserId,
            candidates.Select(candidate => candidate.Row.PoiId).ToArray(),
            candidates.Select(candidate => candidate.Row.CategoryId).ToArray(),
            cancellationToken);
        var behaviorScorer = new PersonalBehaviorAffinityScorer(_options);
        var baseScorer = new PersonalizationBaseScorer(_options);
        var scoredCandidates = candidates
            .Select(candidate =>
            {
                var features = PersonalizationFeatureBuilder.Build(
                    preferenceTokens,
                    candidate.Row.CategoryName,
                    candidate.Row.TagNames,
                    candidate.Row.ScenicScore,
                    candidate.Row.PhotoRating);
                var behaviorAffinity = behaviorScorer.Score(
                    candidate.Row.PoiId,
                    candidate.Row.CategoryId,
                    aggregation);
                return new ScoredCandidate(
                    candidate,
                    features,
                    behaviorAffinity,
                    baseScorer.Score(features, behaviorAffinity));
            })
            .OrderByDescending(candidate => candidate.BaseScore)
            .ThenByDescending(candidate =>
                candidate.Candidate.Row.ScenicScore ?? decimal.MinValue)
            .ThenByDescending(candidate =>
                candidate.Candidate.Row.PhotoRating ?? decimal.MinValue)
            .ThenBy(candidate => candidate.Candidate.DistanceKm)
            .ThenBy(candidate =>
                candidate.Candidate.Row.EstimatedVisitCost ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.Candidate.Row.PoiId)
            .Take(query.Limit ?? 10)
            .ToArray();
        var selectedPoiIds = scoredCandidates
            .Select(candidate => candidate.Candidate.Row.PoiId)
            .ToArray();
        var displayMetadata = await _dbContext.PointsOfInterest
            .AsNoTracking()
            .Where(poi => selectedPoiIds.Contains(poi.Id))
            .Select(poi => new DisplayMetadata(
                poi.Id,
                _dbContext.PoiPhotos
                    .Where(photo => photo.PointOfInterestId == poi.Id)
                    .OrderBy(photo => photo.SortOrder)
                    .ThenBy(photo => photo.Id)
                    .Select(photo => photo.Url)
                    .FirstOrDefault(),
                _dbContext.Reviews
                    .Where(review =>
                        review.TargetType == Review.TargetTypePoi
                        && review.TargetId == poi.Id)
                    .Average(review => (decimal?)review.Rating)))
            .ToDictionaryAsync(metadata => metadata.PoiId, cancellationToken);
        var items = scoredCandidates
            .Select(candidate =>
            {
                var metadata = displayMetadata[candidate.Candidate.Row.PoiId];
                return new RecommendedPoiItemDto(
                    candidate.Candidate.Row.PoiId,
                    candidate.Candidate.Row.Name,
                    candidate.Candidate.Row.CategoryName,
                    metadata.ThumbnailUrl,
                    Math.Round(
                        candidate.Candidate.DistanceKm,
                        2,
                        MidpointRounding.AwayFromZero),
                    candidate.Candidate.Row.EstimatedVisitCost,
                    metadata.AverageRating.HasValue
                        ? Math.Round(
                            metadata.AverageRating.Value,
                            1,
                            MidpointRounding.AwayFromZero)
                        : null,
                    BuildRecommendationReason(
                        candidate.Features,
                        candidate.BehaviorAffinity,
                        candidate.Candidate.Row.CategoryName));
            })
            .ToArray();

        return Result.Success(new PoiRecommendationResultDto(items, candidates.Length));
    }

    private sealed record CandidateRow(
        long PoiId,
        int CategoryId,
        string Name,
        string CategoryName,
        IReadOnlyCollection<string> TagNames,
        decimal Latitude,
        decimal Longitude,
        decimal? ScenicScore,
        decimal? PhotoRating,
        decimal? EstimatedVisitCost);

    private sealed record Candidate(CandidateRow Row, decimal DistanceKm);

    private static string? BuildRecommendationReason(
        PersonalizationPoiFeatures features,
        decimal behaviorAffinity,
        string categoryName)
    {
        var displayCategoryName = LocalizeCategoryName(categoryName);
        if (features.CategoryAffinity > 0m
            && features.TagAffinity > 0m
            && behaviorAffinity > 0.5m)
        {
            return $"Phù hợp với sở thích {displayCategoryName}, các thẻ quan tâm và lịch sử của bạn";
        }

        if (features.CategoryAffinity > 0m && behaviorAffinity > 0.5m)
        {
            return $"Phù hợp với sở thích {displayCategoryName} và lịch sử tương tác của bạn";
        }

        if (features.CategoryAffinity > 0m && features.TagAffinity > 0m)
        {
            return $"Phù hợp với danh mục {displayCategoryName} và các thẻ sở thích của bạn";
        }

        if (features.CategoryAffinity > 0m)
        {
            return $"Phù hợp với sở thích {displayCategoryName} của bạn";
        }

        if (features.TagAffinity > 0m)
        {
            return "Phù hợp với các thẻ quan tâm của bạn";
        }

        if (behaviorAffinity > 0.5m)
        {
            return "Phù hợp với lịch sử tương tác của bạn";
        }

        if (features.ScenicQuality >= 0.8m || features.PhotoQuality >= 0.8m)
        {
            return "Địa điểm được đánh giá cao tại khu vực tìm kiếm";
        }

        return null;
    }

    private static string LocalizeCategoryName(string categoryName) => categoryName.Trim() switch
    {
        "Culture" => "Văn hóa",
        "Museum" => "Bảo tàng",
        "Nature" => "Thiên nhiên",
        "Natural attraction" => "Điểm tham quan thiên nhiên",
        "Beach" => "Bãi biển",
        "Food" => "Ẩm thực",
        "Entertainment" => "Giải trí",
        "Theme park" => "Công viên chủ đề",
        "Water park" => "Công viên nước",
        "Spiritual" => "Tâm linh",
        "Historical" => "Lịch sử",
        _ => categoryName,
    };

    private sealed record ScoredCandidate(
        Candidate Candidate,
        PersonalizationPoiFeatures Features,
        decimal BehaviorAffinity,
        decimal BaseScore);

    private sealed record DisplayMetadata(
        long PoiId,
        string? ThumbnailUrl,
        decimal? AverageRating);
}
using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>A durable snapshot of the criteria used by one explicit UC-25 refresh.</summary>
public sealed class TourSearchRequest : BaseEntity
{
    public const int DestinationTextMaxLength = 300;
    public const int InterestTagsJsonMaxLength = 1000;

    private readonly List<TourRecommendationLog> _recommendations = [];

    private TourSearchRequest()
    {
    }

    public long TravelerUserId { get; private set; }

    public User TravelerUser { get; private set; } = null!;

    public string? DestinationText { get; private set; }

    public DateOnly? StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    public decimal? MinBudget { get; private set; }

    public decimal? MaxBudget { get; private set; }

    public string? InterestTagsJson { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public IReadOnlyCollection<TourRecommendationLog> Recommendations => _recommendations;

    public static TourSearchRequest Create(
        long travelerUserId,
        string? destinationText,
        DateOnly? startDate,
        DateOnly? endDate,
        decimal? minBudget,
        decimal? maxBudget,
        string interestTagsJson,
        DateTimeOffset requestedAtUtc)
    {
        if (travelerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        }

        if (startDate.HasValue && endDate.HasValue && startDate > endDate)
        {
            throw new ArgumentException("The start date cannot be after the end date.", nameof(startDate));
        }

        ValidateBudget(minBudget, nameof(minBudget));
        ValidateBudget(maxBudget, nameof(maxBudget));
        if (minBudget.HasValue && maxBudget.HasValue && minBudget > maxBudget)
        {
            throw new ArgumentException("The minimum budget cannot exceed the maximum budget.", nameof(minBudget));
        }

        string? normalizedDestination = NormalizeOptional(destinationText);
        if (normalizedDestination?.Length > DestinationTextMaxLength)
        {
            throw new ArgumentException("Destination exceeds its database column length.", nameof(destinationText));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(interestTagsJson);
        string normalizedTags = interestTagsJson.Trim();
        if (normalizedTags.Length > InterestTagsJsonMaxLength)
        {
            throw new ArgumentException("Interest tags exceed their database column length.", nameof(interestTagsJson));
        }

        return new TourSearchRequest
        {
            TravelerUserId = travelerUserId,
            DestinationText = normalizedDestination,
            StartDate = startDate,
            EndDate = endDate,
            MinBudget = minBudget,
            MaxBudget = maxBudget,
            InterestTagsJson = normalizedTags,
            RequestedAtUtc = requestedAtUtc.ToUniversalTime(),
        };
    }

    public TourRecommendationLog AddRecommendation(
        long tourId,
        decimal similarityScore,
        decimal configuredThreshold,
        DateTimeOffset recommendedAtUtc)
    {
        var recommendation = TourRecommendationLog.Create(
            this,
            tourId,
            similarityScore,
            configuredThreshold,
            recommendedAtUtc);
        _recommendations.Add(recommendation);
        return recommendation;
    }

    private static void ValidateBudget(decimal? budget, string parameterName)
    {
        if (budget < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
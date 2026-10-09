namespace TripMate.Application.Features.TripReviews.Common;

/// <summary>
/// Screen the validated, normalized command text without modifying it. Callers must
/// authorize and check duplicates/edit eligibility before invoking it. Cancellation
/// propagates as OperationCanceledException, not approval or evaluator unavailability.
/// </summary>
public interface IReviewContentModerator
{
    string ActivePolicyVersion { get; }

    Task<ReviewContentModerationResult> ScreenAsync(ReviewText text, CancellationToken cancellationToken);
}

public static class ReviewContentPolicy
{
    public const string ActiveVersion = "tm79-review-text-v1";
}

public enum ReviewModerationDecision { Accepted, Rejected, Unavailable }

public enum ReviewPolicyCategory
{
    ThreatOrCallForPhysicalHarm,
    TargetedDegradingHarassment,
    HateOrExclusionBasedOnProtectedCharacteristic,
    UnrelatedExplicitSexualContent,
    TargetedPrivateContactOrResidentialDisclosureForHarm,
    UnrelatedAdvertisingSpamPhishingScamOrFraudulentSolicitation,
}

public sealed record ReviewContentModerationResult
{
    private static readonly IReadOnlyList<ReviewPolicyCategory> NoCategories =
        Array.AsReadOnly(Array.Empty<ReviewPolicyCategory>());

    private ReviewContentModerationResult(
        ReviewModerationDecision decision,
        string? policyVersion,
        IReadOnlyList<ReviewPolicyCategory> categories)
    {
        Decision = decision;
        PolicyVersion = policyVersion;
        Categories = categories;
    }

    public ReviewModerationDecision Decision { get; }
    public string? PolicyVersion { get; }
    public IReadOnlyList<ReviewPolicyCategory> Categories { get; }

    public static ReviewContentModerationResult Accepted(string policyVersion)
    {
        if (string.IsNullOrWhiteSpace(policyVersion)) throw new ArgumentException("An identified accepted policy version is required.", nameof(policyVersion));
        return new(ReviewModerationDecision.Accepted, policyVersion, NoCategories);
    }

    public static ReviewContentModerationResult Rejected(params ReviewPolicyCategory[] categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        ReviewPolicyCategory[] stableCategories = categories
            .Distinct()
            .Order()
            .ToArray();
        if (stableCategories.Length == 0
            || stableCategories.Any(category => !Enum.IsDefined(category)))
        {
            throw new ArgumentException(
                "At least one identified policy category is required.",
                nameof(categories));
        }

        return new(
            ReviewModerationDecision.Rejected,
            null,
            Array.AsReadOnly(stableCategories));
    }

    public static ReviewContentModerationResult Unavailable() =>
        new(ReviewModerationDecision.Unavailable, null, NoCategories);
}
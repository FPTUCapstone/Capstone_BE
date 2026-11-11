using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Personalization;

public sealed class PersonalizationRankingOptions
{
    public const string SectionName = "Personalization";

    public decimal CategoryAffinityWeight { get; init; } = 0.450m;

    public decimal TagAffinityWeight { get; init; } = 0.350m;

    public decimal BehaviorAffinityWeight { get; init; } = 0.100m;

    public decimal ScenicQualityWeight { get; init; } = 0.050m;

    public decimal PhotoQualityWeight { get; init; } = 0.050m;

    public decimal BaseWeight { get; init; } = 0.55m;

    public decimal AiWeight { get; init; } = 0.45m;

    public decimal SkipWeight { get; init; } = 0.5m;

    public decimal PriorWeight { get; init; } = 2.0m;

    public int MinCategoryDistinctPoiCount { get; init; } = 2;

    public int MaxProviderCandidates { get; init; } = 60;

    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public decimal PersonalizedAiFallbackScore { get; init; } = 0.60m;

    public decimal DefaultAiFallbackScore { get; init; } = 0.50m;

    // UC-10 item 6: the behavior profile sent to the AI ranking (spec .scratch/uc10-behavior-learning §3.3).

    /// <summary>A signal weighs half as much after this many days (decision D3).</summary>
    public int BehaviorHalfLifeDays { get; init; } = 90;

    /// <summary>Older signals are ignored: after a year a signal weighs under 6% of a fresh one.</summary>
    public int BehaviorLookbackDays { get; init; } = 365;

    /// <summary>Most recent signals read per traveler.</summary>
    public int MaxBehaviorEvents { get; init; } = 500;

    /// <summary>Liked (and disliked) POIs sent to the AI; the AI service keeps at most 50 per side.</summary>
    public int MaxBehaviorPoisPerSide { get; init; } = 50;

    /// <summary>
    /// Decision D8: a stop the traveler removed from an itinerary or skipped during navigation is not suggested
    /// automatically again for this many days, unless they choose it again. 0 turns the rule off.
    /// </summary>
    public int RemovedStopCooldownDays { get; init; } = 14;

    public BehaviorSignalWeights BehaviorSignalWeights { get; init; } = new();
}

/// <summary>
/// How much one behavior signal counts before time decay (decisions D1 and D6). A Like-type signal adds to a
/// POI, a Dislike or Skip subtracts; the per-POI net decides whether it is sent as liked or disliked.
/// </summary>
public sealed class BehaviorSignalWeights
{
    public decimal ExplicitLike { get; init; } = 1.0m;

    public decimal ExplicitDislike { get; init; } = 1.0m;

    public decimal ExplicitSkip { get; init; } = 0.5m;

    /// <summary>The traveler added the stop while adjusting an itinerary.</summary>
    public decimal StopAdded { get; init; } = 1.0m;

    /// <summary>The traveler removed the stop while adjusting an itinerary.</summary>
    public decimal StopRemoved { get; init; } = 1.0m;

    /// <summary>Reached during navigation: weak, the traveler may simply have followed the plan.</summary>
    public decimal StopReached { get; init; } = 0.3m;

    public decimal StopSkipped { get; init; } = 0.7m;

    /// <summary>Marked mandatory when creating the request (decision D6).</summary>
    public decimal MandatoryChosen { get; init; } = 0.5m;

    /// <summary>Positive for a like, negative for a dislike or skip, zero for a signal that says nothing.</summary>
    public decimal SignedWeight(RecommendationEventType eventType, RecommendationCaptureSource source) =>
        (eventType, source) switch
        {
            (RecommendationEventType.Like, RecommendationCaptureSource.Navigation) => StopReached,
            (RecommendationEventType.Like, RecommendationCaptureSource.SchedulingRequest) => MandatoryChosen,
            (RecommendationEventType.Like, RecommendationCaptureSource.ItineraryEdit) => StopAdded,
            (RecommendationEventType.Like, _) => ExplicitLike,
            (RecommendationEventType.Dislike, RecommendationCaptureSource.ItineraryEdit) => -StopRemoved,
            (RecommendationEventType.Dislike, _) => -ExplicitDislike,
            (RecommendationEventType.Skip, RecommendationCaptureSource.Navigation) => -StopSkipped,
            (RecommendationEventType.Skip, _) => -ExplicitSkip,
            _ => 0m,
        };
}

public sealed class PersonalizationRankingOptionsValidator(
    SchedulingGenerationOptions schedulingGenerationOptions)
    : IValidateOptions<PersonalizationRankingOptions>
{
    private const decimal WeightSumTolerance = 0.001m;

    private readonly SchedulingGenerationOptions _schedulingGenerationOptions =
        schedulingGenerationOptions ?? throw new ArgumentNullException(
            nameof(schedulingGenerationOptions));

    public ValidateOptionsResult Validate(
        string? name,
        PersonalizationRankingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        AddNonNegativeFailure(
            options.CategoryAffinityWeight,
            nameof(options.CategoryAffinityWeight),
            failures);
        AddNonNegativeFailure(
            options.TagAffinityWeight,
            nameof(options.TagAffinityWeight),
            failures);
        AddNonNegativeFailure(
            options.BehaviorAffinityWeight,
            nameof(options.BehaviorAffinityWeight),
            failures);
        AddNonNegativeFailure(
            options.ScenicQualityWeight,
            nameof(options.ScenicQualityWeight),
            failures);
        AddNonNegativeFailure(
            options.PhotoQualityWeight,
            nameof(options.PhotoQualityWeight),
            failures);

        decimal baseWeightSum =
            options.CategoryAffinityWeight
            + options.TagAffinityWeight
            + options.BehaviorAffinityWeight
            + options.ScenicQualityWeight
            + options.PhotoQualityWeight;
        if (Math.Abs(baseWeightSum - 1m) > WeightSumTolerance)
        {
            failures.Add(
                "Base component weights must sum to 1 within a tolerance of 0.001.");
        }

        AddNonNegativeFailure(options.BaseWeight, nameof(options.BaseWeight), failures);
        AddNonNegativeFailure(options.AiWeight, nameof(options.AiWeight), failures);
        if (Math.Abs(options.BaseWeight + options.AiWeight - 1m) > WeightSumTolerance)
        {
            failures.Add("BaseWeight and AiWeight must sum to 1 within a tolerance of 0.001.");
        }

        AddNonNegativeFailure(options.SkipWeight, nameof(options.SkipWeight), failures);
        if (options.PriorWeight <= 0m)
        {
            failures.Add($"{nameof(options.PriorWeight)} must be greater than zero.");
        }

        if (options.MinCategoryDistinctPoiCount < 1)
        {
            failures.Add(
                $"{nameof(options.MinCategoryDistinctPoiCount)} must be at least 1.");
        }

        if (options.MaxProviderCandidates <
            _schedulingGenerationOptions.EffectiveMaxMatrixCandidates)
        {
            failures.Add(
                $"{nameof(options.MaxProviderCandidates)} must be greater than or equal to "
                + nameof(SchedulingGenerationOptions.EffectiveMaxMatrixCandidates) + ".");
        }

        if (options.ProviderTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.ProviderTimeout)} must be greater than zero.");
        }

        AddUnitIntervalFailure(
            options.PersonalizedAiFallbackScore,
            nameof(options.PersonalizedAiFallbackScore),
            failures);
        AddUnitIntervalFailure(
            options.DefaultAiFallbackScore,
            nameof(options.DefaultAiFallbackScore),
            failures);

        AddPositiveFailure(options.BehaviorHalfLifeDays, nameof(options.BehaviorHalfLifeDays), failures);
        AddPositiveFailure(options.BehaviorLookbackDays, nameof(options.BehaviorLookbackDays), failures);
        AddPositiveFailure(options.MaxBehaviorEvents, nameof(options.MaxBehaviorEvents), failures);
        if (options.MaxBehaviorPoisPerSide is < 0 or > 50)
        {
            failures.Add($"{nameof(options.MaxBehaviorPoisPerSide)} must be between 0 and 50.");
        }

        if (options.RemovedStopCooldownDays < 0)
        {
            failures.Add($"{nameof(options.RemovedStopCooldownDays)} must be greater than or equal to zero.");
        }

        BehaviorSignalWeights signals = options.BehaviorSignalWeights;
        foreach (var (value, propertyName) in new[]
                 {
                     (signals.ExplicitLike, nameof(signals.ExplicitLike)),
                     (signals.ExplicitDislike, nameof(signals.ExplicitDislike)),
                     (signals.ExplicitSkip, nameof(signals.ExplicitSkip)),
                     (signals.StopAdded, nameof(signals.StopAdded)),
                     (signals.StopRemoved, nameof(signals.StopRemoved)),
                     (signals.StopReached, nameof(signals.StopReached)),
                     (signals.StopSkipped, nameof(signals.StopSkipped)),
                     (signals.MandatoryChosen, nameof(signals.MandatoryChosen)),
                 })
        {
            AddNonNegativeFailure(value, $"{nameof(options.BehaviorSignalWeights)}.{propertyName}", failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void AddNonNegativeFailure(
        decimal value,
        string propertyName,
        ICollection<string> failures)
    {
        if (value < 0m)
        {
            failures.Add($"{propertyName} must be greater than or equal to zero.");
        }
    }

    private static void AddPositiveFailure(int value, string propertyName, ICollection<string> failures)
    {
        if (value < 1)
        {
            failures.Add($"{propertyName} must be at least 1.");
        }
    }

    private static void AddUnitIntervalFailure(
        decimal value,
        string propertyName,
        ICollection<string> failures)
    {
        if (value is < 0m or > 1m)
        {
            failures.Add($"{propertyName} must be between 0 and 1.");
        }
    }
}
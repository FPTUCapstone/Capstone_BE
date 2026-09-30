using Microsoft.Extensions.Options;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.Features.Scheduling.Personalization;

public sealed class PersonalizationRankingOptions
{
    public const string SectionName = "Personalization";

    public decimal CategoryAffinityWeight { get; init; } = 0.300m;

    public decimal TagAffinityWeight { get; init; } = 0.200m;

    public decimal BehaviorAffinityWeight { get; init; } = 0.250m;

    public decimal ScenicQualityWeight { get; init; } = 0.125m;

    public decimal PhotoQualityWeight { get; init; } = 0.125m;

    public decimal BaseWeight { get; init; } = 0.60m;

    public decimal AiWeight { get; init; } = 0.40m;

    public decimal SkipWeight { get; init; } = 0.5m;

    public decimal PriorWeight { get; init; } = 2.0m;

    public int MinCategoryDistinctPoiCount { get; init; } = 2;

    public int MaxProviderCandidates { get; init; } = 60;

    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(5);
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
}
using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed class SchedulingGenerationOptionsValidator : IValidateOptions<SchedulingGenerationOptions>
{
    public ValidateOptionsResult Validate(string? name, SchedulingGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.TransitionBufferMinutes < 0)
        {
            failures.Add($"{nameof(options.TransitionBufferMinutes)} must be non-negative.");
        }

        if (options.FinalReturnBufferMinutes < 0)
        {
            failures.Add($"{nameof(options.FinalReturnBufferMinutes)} must be non-negative.");
        }

        if (options.MaxMatrixCandidates < SchedulingGenerationOptions.MinimumMaxMatrixCandidates)
        {
            failures.Add($"{nameof(options.MaxMatrixCandidates)} must be at least {SchedulingGenerationOptions.MinimumMaxMatrixCandidates}.");
        }

        if (options.MaxRouteOptimizationSeeds is < SchedulingGenerationOptions.MinRouteOptimizationSeeds or > SchedulingGenerationOptions.MaxAllowedRouteOptimizationSeeds)
        {
            failures.Add($"{nameof(options.MaxRouteOptimizationSeeds)} must be between {SchedulingGenerationOptions.MinRouteOptimizationSeeds} and {SchedulingGenerationOptions.MaxAllowedRouteOptimizationSeeds}.");
        }

        if (options.MaxRouteEvaluations is < SchedulingGenerationOptions.MinRouteEvaluations or > SchedulingGenerationOptions.MaxAllowedRouteEvaluations)
        {
            failures.Add($"{nameof(options.MaxRouteEvaluations)} must be between {SchedulingGenerationOptions.MinRouteEvaluations} and {SchedulingGenerationOptions.MaxAllowedRouteEvaluations}.");
        }

        if (!Enum.IsDefined(options.SolverMode))
        {
            failures.Add($"{nameof(options.SolverMode)} must be one of {string.Join(", ", Enum.GetNames<SchedulingSolverMode>())}.");
        }

        AddIfNotPositive(failures, nameof(options.Csp.MaxNodes), options.Csp.MaxNodes);
        AddIfNotPositive(failures, nameof(options.Csp.TimeLimitMilliseconds), options.Csp.TimeLimitMilliseconds);
        AddIfNotPositive(failures, nameof(options.Csp.MaxOptionalDomainSize), options.Csp.MaxOptionalDomainSize);
        AddIfNotPositive(failures, nameof(options.Csp.MaxStops), options.Csp.MaxStops);

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void AddIfNotPositive(List<string> failures, string cspOption, int value)
    {
        if (value <= 0)
        {
            failures.Add($"{nameof(SchedulingGenerationOptions.Csp)}.{cspOption} must be greater than zero.");
        }
    }
}
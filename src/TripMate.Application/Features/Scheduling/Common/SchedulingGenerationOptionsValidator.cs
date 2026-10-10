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

        const string csp = nameof(SchedulingGenerationOptions.Csp);
        AddIfNotPositive(failures, csp, nameof(options.Csp.MaxNodes), options.Csp.MaxNodes);
        AddIfNotPositive(failures, csp, nameof(options.Csp.TimeLimitMilliseconds), options.Csp.TimeLimitMilliseconds);
        AddIfNotPositive(failures, csp, nameof(options.Csp.MaxOptionalDomainSize), options.Csp.MaxOptionalDomainSize);
        AddIfNotPositive(failures, csp, nameof(options.Csp.MaxStops), options.Csp.MaxStops);
        AddIfNotPositive(failures, csp, nameof(options.Csp.FinalCandidatesToValidate), options.Csp.FinalCandidatesToValidate);

        // MiniRouting dùng cho cả SolverMode.MiniRouting lẫn mức phạt bỏ điểm và bước polish của CSP.
        const string miniRouting = nameof(SchedulingGenerationOptions.MiniRouting);
        var routing = options.MiniRouting;
        AddIfNotPositive(failures, miniRouting, nameof(routing.MaxSkipPenaltyMinutes), routing.MaxSkipPenaltyMinutes);
        AddIfNotPositive(failures, miniRouting, nameof(routing.MaxGlsIterations), routing.MaxGlsIterations);
        AddIfNotPositive(failures, miniRouting, nameof(routing.TimeLimitMilliseconds), routing.TimeLimitMilliseconds);
        AddIfNotPositive(failures, miniRouting, nameof(routing.MaxOrOptSegmentLength), routing.MaxOrOptSegmentLength);
        AddIfNotPositive(failures, miniRouting, nameof(routing.FinalCandidatesToValidate), routing.FinalCandidatesToValidate);
        if (!double.IsFinite(routing.GlsLambdaFactor) || routing.GlsLambdaFactor < 0)
        {
            failures.Add($"{miniRouting}.{nameof(routing.GlsLambdaFactor)} must be a finite, non-negative number.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void AddIfNotPositive(List<string> failures, string section, string option, int value)
    {
        if (value <= 0)
        {
            failures.Add($"{section}.{option} must be greater than zero.");
        }
    }
}
using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.AiRanking;

public sealed class PoiRankingProviderOptionsValidator
    : IValidateOptions<PoiRankingProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, PoiRankingProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        ValidateBudget(options, failures);
        if (!options.Enabled)
        {
            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add(
                $"{nameof(options.Endpoint)} must be an absolute HTTPS URI when AI ranking is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add(
                $"{nameof(options.ApiKey)} is required when AI ranking is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.ModelName))
        {
            failures.Add(
                $"{nameof(options.ModelName)} is required when AI ranking is enabled.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateBudget(PoiRankingProviderOptions options, List<string> failures)
    {
        if (options.RateLimitPerMinute is < 1 or > 1000)
        {
            failures.Add($"{nameof(options.RateLimitPerMinute)} must be between 1 and 1000.");
        }

        if (options.MaxConcurrency is < 1 or > 100)
        {
            failures.Add($"{nameof(options.MaxConcurrency)} must be between 1 and 100.");
        }
    }
}
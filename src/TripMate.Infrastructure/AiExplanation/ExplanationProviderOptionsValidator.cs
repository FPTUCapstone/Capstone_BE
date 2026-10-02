using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.AiExplanation;

public sealed class ExplanationProviderOptionsValidator
    : IValidateOptions<ExplanationProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, ExplanationProviderOptions options)
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
                $"{nameof(options.Endpoint)} must be an absolute HTTPS URI when AI explanation is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add(
                $"{nameof(options.ApiKey)} is required when AI explanation is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.ModelName))
        {
            failures.Add(
                $"{nameof(options.ModelName)} is required when AI explanation is enabled.");
        }

        if (options.TimeoutSeconds <= 0)
        {
            failures.Add(
                $"{nameof(options.TimeoutSeconds)} must be greater than zero.");
        }

        if (options.OverallTimeoutSeconds <= 0 || options.OverallTimeoutSeconds > 30)
        {
            failures.Add(
                $"{nameof(options.OverallTimeoutSeconds)} must be between 1 and 30 seconds.");
        }

        if (options.TimeoutSeconds > options.OverallTimeoutSeconds)
        {
            failures.Add(
                $"{nameof(options.TimeoutSeconds)} cannot exceed {nameof(options.OverallTimeoutSeconds)}.");
        }

        if (options.MaxAttempts is < 1 or > 2)
        {
            failures.Add(
                $"{nameof(options.MaxAttempts)} must be 1 or 2.");
        }

        if (options.RetryBaseDelayMilliseconds is < 0 or > 5000)
        {
            failures.Add(
                $"{nameof(options.RetryBaseDelayMilliseconds)} must be between 0 and 5000 milliseconds.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateBudget(ExplanationProviderOptions options, List<string> failures)
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
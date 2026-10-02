using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Scheduling.Explanation;

public sealed class ItineraryExplanationExecutionOptions
{
    public static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultOverallTimeout = TimeSpan.FromSeconds(20);
    public const int DefaultMaxAttempts = 2;
    public const int DefaultRetryBaseDelayMilliseconds = 500;

    private TimeSpan? _overallTimeout;

    public bool Enabled { get; set; }

    public TimeSpan ProviderTimeout { get; set; } = DefaultProviderTimeout;

    public TimeSpan OverallTimeout
    {
        get => _overallTimeout ?? (ProviderTimeout != DefaultProviderTimeout ? ProviderTimeout : DefaultOverallTimeout);
        set => _overallTimeout = value;
    }

    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    public int RetryBaseDelayMilliseconds { get; set; } = DefaultRetryBaseDelayMilliseconds;
}

public sealed class ItineraryExplanationExecutionOptionsValidator
    : IValidateOptions<ItineraryExplanationExecutionOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        ItineraryExplanationExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.ProviderTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.ProviderTimeout)} must be greater than zero.");
        }

        if (options.OverallTimeout <= TimeSpan.Zero || options.OverallTimeout > TimeSpan.FromSeconds(30))
        {
            failures.Add($"{nameof(options.OverallTimeout)} must be between 1 and 30 seconds.");
        }

        if (options.ProviderTimeout > options.OverallTimeout)
        {
            failures.Add($"{nameof(options.ProviderTimeout)} cannot exceed {nameof(options.OverallTimeout)}.");
        }

        if (options.MaxAttempts is < 1 or > 2)
        {
            failures.Add($"{nameof(options.MaxAttempts)} must be 1 or 2.");
        }

        if (options.RetryBaseDelayMilliseconds is < 0 or > 5000)
        {
            failures.Add($"{nameof(options.RetryBaseDelayMilliseconds)} must be between 0 and 5000 milliseconds.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
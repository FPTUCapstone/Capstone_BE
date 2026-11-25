using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Scheduling.Explanation;

public sealed class ItineraryExplanationExecutionOptions
{
    public static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultOverallTimeout = TimeSpan.FromSeconds(20);
    public const int DefaultMaxAttempts = 2;
    public const int DefaultRetryBaseDelayMilliseconds = 500;
    public static readonly TimeSpan DefaultBackgroundTimeout = TimeSpan.FromSeconds(100);
    public static readonly TimeSpan MaxBackgroundTimeout = TimeSpan.FromSeconds(300);

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

    /// <summary>
    /// Background mode (specs/UC-10-background-explanation-spec.md): the create request answers with the template
    /// sentences and the AI explanation is written afterwards. Has no effect while <see cref="Enabled"/> is false.
    /// </summary>
    public bool BackgroundEnabled { get; set; }

    /// <summary>
    /// How long one background explanation may take. Separate from <see cref="OverallTimeout"/>, whose 30-second
    /// ceiling protects the request the traveler waits on. Keep it longer than the AI service's own limit, so the
    /// backend is still listening when a slow answer arrives.
    /// </summary>
    public TimeSpan BackgroundTimeout { get; set; } = DefaultBackgroundTimeout;
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

        if (options.BackgroundTimeout < TimeSpan.FromSeconds(1)
            || options.BackgroundTimeout > ItineraryExplanationExecutionOptions.MaxBackgroundTimeout)
        {
            failures.Add($"{nameof(options.BackgroundTimeout)} must be between 1 and 300 seconds.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
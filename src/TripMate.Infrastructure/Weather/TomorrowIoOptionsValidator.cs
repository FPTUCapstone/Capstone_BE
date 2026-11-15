using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Weather;

public sealed class TomorrowIoOptionsValidator : IValidateOptions<TomorrowIoOptions>
{
    public ValidateOptionsResult Validate(string? name, TomorrowIoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        RequirePositive(failures, options.WebhookToleranceSeconds, nameof(options.WebhookToleranceSeconds));
        RequirePositive(failures, options.MaxPayloadSizeBytes, nameof(options.MaxPayloadSizeBytes));
        RequirePositive(failures, options.ReconciliationIntervalMinutes, nameof(options.ReconciliationIntervalMinutes));
        RequirePositive(failures, options.ReconciliationLookbackMinutes, nameof(options.ReconciliationLookbackMinutes));
        RequirePositive(failures, options.LocationSyncIntervalHours, nameof(options.LocationSyncIntervalHours));
        RequirePositive(failures, options.LocationMonitoringLookaheadDays, nameof(options.LocationMonitoringLookaheadDays));
        RequirePositive(failures, options.SafetyReviewAcknowledgementSlaMinutes, nameof(options.SafetyReviewAcknowledgementSlaMinutes));

        // A disabled integration needs no credentials; an enabled one cannot run without them.
        if (options.Enabled)
        {
            // ApiKey is already resolved here: it falls back to Tomorrow:ApiKey in PostConfigure.
            RequireValue(failures, options.ApiKey, nameof(options.ApiKey));
            RequireValue(failures, options.WebhookSecret, nameof(options.WebhookSecret));
            RequireValue(failures, options.AlertId, nameof(options.AlertId));
            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl)
                || baseUrl.Scheme != Uri.UriSchemeHttps)
            {
                failures.Add($"{nameof(options.BaseUrl)} must be an absolute https URL.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(List<string> failures, int value, string name)
    {
        if (value <= 0)
        {
            failures.Add($"{name} must be greater than zero.");
        }
    }

    // Names the setting only; a secret value is never echoed.
    private static void RequireValue(List<string> failures, string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{name} is required when Tomorrow.io alerts are enabled.");
        }
    }
}
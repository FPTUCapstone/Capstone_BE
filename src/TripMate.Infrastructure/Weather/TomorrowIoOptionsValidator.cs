using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Weather;

public sealed class TomorrowIoOptionsValidator : IValidateOptions<TomorrowIoOptions>
{
    public ValidateOptionsResult Validate(string? name, TomorrowIoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.WebhookToleranceSeconds <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(options.WebhookToleranceSeconds)} must be greater than zero.");
        }

        if (options.MaxPayloadSizeBytes <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(options.MaxPayloadSizeBytes)} must be greater than zero.");
        }

        if (options.ReconciliationIntervalMinutes <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(options.ReconciliationIntervalMinutes)} must be greater than zero.");
        }

        if (options.LocationSyncIntervalHours <= 0)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(options.LocationSyncIntervalHours)} must be greater than zero.");
        }

        return ValidateOptionsResult.Success;
    }
}

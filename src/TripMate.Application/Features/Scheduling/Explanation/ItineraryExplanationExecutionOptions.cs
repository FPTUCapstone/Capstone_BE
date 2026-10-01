using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Scheduling.Explanation;

public sealed class ItineraryExplanationExecutionOptions
{
    public static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(5);

    public TimeSpan ProviderTimeout { get; init; } = DefaultProviderTimeout;
}

public sealed class ItineraryExplanationExecutionOptionsValidator
    : IValidateOptions<ItineraryExplanationExecutionOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        ItineraryExplanationExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.ProviderTimeout > TimeSpan.Zero
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{nameof(options.ProviderTimeout)} must be greater than zero.");
    }
}
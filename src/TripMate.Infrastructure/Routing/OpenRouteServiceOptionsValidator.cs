using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Routing;

/// <summary>Fails startup on a motorbike duration factor outside the supported range instead of ignoring it.</summary>
public sealed class OpenRouteServiceOptionsValidator : IValidateOptions<OpenRouteServiceOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenRouteServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MotorbikeDurationFactor is >= OpenRouteServiceOptions.MinMotorbikeDurationFactor
            and <= OpenRouteServiceOptions.MaxMotorbikeDurationFactor
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{nameof(options.MotorbikeDurationFactor)} must be between "
                + $"{OpenRouteServiceOptions.MinMotorbikeDurationFactor} and "
                + $"{OpenRouteServiceOptions.MaxMotorbikeDurationFactor}.");
    }
}
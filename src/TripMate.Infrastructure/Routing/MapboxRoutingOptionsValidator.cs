using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Routing;

/// <summary>
/// Fails startup on a motorbike duration factor outside the supported range or a base URL that is not
/// an absolute http(s) address, instead of failing later on the first routing call.
/// </summary>
public sealed class MapboxRoutingOptionsValidator : IValidateOptions<MapboxRoutingOptions>
{
    public ValidateOptionsResult Validate(string? name, MapboxRoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.MotorbikeDurationFactor is not (>= MapboxRoutingOptions.MinMotorbikeDurationFactor
            and <= MapboxRoutingOptions.MaxMotorbikeDurationFactor))
        {
            failures.Add(
                $"{nameof(options.MotorbikeDurationFactor)} must be between "
                + $"{MapboxRoutingOptions.MinMotorbikeDurationFactor} and "
                + $"{MapboxRoutingOptions.MaxMotorbikeDurationFactor}.");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add($"{nameof(options.BaseUrl)} must be an absolute http(s) URL.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
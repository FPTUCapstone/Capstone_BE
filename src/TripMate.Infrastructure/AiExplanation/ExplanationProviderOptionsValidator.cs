using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.AiExplanation;

public sealed class ExplanationProviderOptionsValidator
    : IValidateOptions<ExplanationProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, ExplanationProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
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

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
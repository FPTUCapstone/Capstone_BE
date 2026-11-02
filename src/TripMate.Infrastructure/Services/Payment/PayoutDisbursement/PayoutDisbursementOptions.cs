using Microsoft.Extensions.Options;

namespace TripMate.Infrastructure.Services.Payment.PayoutDisbursement;

/// <summary>
/// Which gateway pays Operators (UC-65). Defaults to the mock so Development, Test and CI can never
/// move money. The PayOS payout channel has its own credentials, separate from the collection channel
/// (<c>Payment:PayOs</c>), and nothing here falls back to those.
/// </summary>
public sealed class PayoutDisbursementOptions
{
    public const string SectionName = "Payment:Payout";
    public const string MockProvider = "Mock";
    public const string PayOsProvider = "PayOs";

    public string Provider { get; init; } = MockProvider;

    public PayOsPayoutOptions PayOs { get; init; } = new();

    public bool UsesPayOs => string.Equals(Provider, PayOsProvider, StringComparison.OrdinalIgnoreCase);

    public bool UsesMock => string.Equals(Provider, MockProvider, StringComparison.OrdinalIgnoreCase);
}

public sealed class PayOsPayoutOptions
{
    public const string DefaultBaseUrl = "https://api-merchant.payos.vn";

    /// <summary>Second switch. A real transfer needs the provider set to PayOs and this turned on.</summary>
    public bool LiveTransfersEnabled { get; init; }

    public string ClientId { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string ChecksumKey { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = DefaultBaseUrl;
}

public sealed class PayoutDisbursementOptionsValidator : IValidateOptions<PayoutDisbursementOptions>
{
    public ValidateOptionsResult Validate(string? name, PayoutDisbursementOptions options)
    {
        if (options.UsesMock)
        {
            return ValidateOptionsResult.Success;
        }

        if (!options.UsesPayOs)
        {
            return ValidateOptionsResult.Fail(
                $"{PayoutDisbursementOptions.SectionName}:Provider must be '{PayoutDisbursementOptions.MockProvider}' "
                + $"or '{PayoutDisbursementOptions.PayOsProvider}'.");
        }

        // Messages name settings, never values.
        var failures = new List<string>();
        var prefix = $"{PayoutDisbursementOptions.SectionName}:PayOs";
        if (!options.PayOs.LiveTransfersEnabled)
        {
            failures.Add($"{prefix}:LiveTransfersEnabled must be true to send real payouts with the PayOs provider.");
        }

        if (string.IsNullOrWhiteSpace(options.PayOs.ClientId))
            failures.Add($"{prefix}:ClientId is required for the PayOs provider.");
        if (string.IsNullOrWhiteSpace(options.PayOs.ApiKey))
            failures.Add($"{prefix}:ApiKey is required for the PayOs provider.");
        if (string.IsNullOrWhiteSpace(options.PayOs.ChecksumKey))
            failures.Add($"{prefix}:ChecksumKey is required for the PayOs provider.");
        if (!Uri.TryCreate(options.PayOs.BaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps)
            failures.Add($"{prefix}:BaseUrl must be an absolute https URL.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
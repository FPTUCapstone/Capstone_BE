using Microsoft.Extensions.Configuration;

using TripMate.Application.Features.Traveler.Payments.Common;

namespace TripMate.Infrastructure.Services.Payment;

/// <summary>
/// Reads payment settings while supporting the pre-UC-28 <c>PayOS</c> secret section.
/// </summary>
public static class PaymentOptionsConfiguration
{
    public const string LegacyPayOsSectionName = "PayOS";

    public static PaymentOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(PaymentOptions.SectionName).Get<PaymentOptions>() ?? new PaymentOptions();
        var legacyPayOs = configuration.GetSection(LegacyPayOsSectionName).Get<PayOsOptions>();
        ApplyLegacyPayOs(options, legacyPayOs);
        return options;
    }

    public static void ApplyLegacyPayOs(PaymentOptions options, PayOsOptions? legacyPayOs)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (legacyPayOs is null)
        {
            return;
        }

        options.PayOs.ClientId = FirstNonEmpty(options.PayOs.ClientId, legacyPayOs.ClientId);
        options.PayOs.ApiKey = FirstNonEmpty(options.PayOs.ApiKey, legacyPayOs.ApiKey);
        options.PayOs.ChecksumKey = FirstNonEmpty(options.PayOs.ChecksumKey, legacyPayOs.ChecksumKey);
        options.PayOs.ReturnUrl = FirstNonEmpty(options.PayOs.ReturnUrl, legacyPayOs.ReturnUrl);
        options.PayOs.CancelUrl = FirstNonEmpty(options.PayOs.CancelUrl, legacyPayOs.CancelUrl);
    }

    private static string FirstNonEmpty(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}

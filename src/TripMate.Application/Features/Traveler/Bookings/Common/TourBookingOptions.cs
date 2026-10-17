using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Traveler.Bookings.Common;

/// <summary>
/// UC-27 configuration values the BE spec explicitly calls out as pending product approval
/// (payment expiry duration, max concurrent unpaid bookings). Kept as configuration — never
/// hardcoded business constants — so product can change them without a code deploy.
/// Defaults match the figures already used in the Mobile/BE specs pending formal sign-off.
/// </summary>
public sealed class TourBookingOptions
{
    public const string SectionName = "TourBooking";

    public TimeSpan PaymentExpiry { get; init; } = TimeSpan.FromMinutes(15);

    public int MaxUnpaidBookingsPerTraveler { get; init; } = 3;
}

public sealed class TourBookingOptionsValidator : IValidateOptions<TourBookingOptions>
{
    public ValidateOptionsResult Validate(string? name, TourBookingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.PaymentExpiry <= TimeSpan.Zero)
            failures.Add($"{nameof(options.PaymentExpiry)} must be greater than zero.");
        if (options.MaxUnpaidBookingsPerTraveler <= 0)
            failures.Add($"{nameof(options.MaxUnpaidBookingsPerTraveler)} must be greater than zero.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

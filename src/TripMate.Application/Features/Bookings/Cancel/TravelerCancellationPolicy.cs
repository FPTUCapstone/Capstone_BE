using System.Text.Json;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.Cancel;

public sealed record TravelerCancellationPolicy(decimal AtLeastSevenDaysPercent,
    decimal ThreeToSevenDaysPercent, decimal OneToThreeDaysPercent)
{
    public const string DefaultSnapshot = Booking.DefaultCancellationPolicySnapshot;

    public static bool TryParse(string snapshot, out TravelerCancellationPolicy? policy)
    {
        policy = null;
        try
        {
            policy = JsonSerializer.Deserialize<TravelerCancellationPolicy>(snapshot,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return policy is not null
                && policy.AtLeastSevenDaysPercent is >= 0 and <= 100
                && policy.ThreeToSevenDaysPercent is >= 0 and <= 100
                && policy.OneToThreeDaysPercent is >= 0 and <= 100;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public decimal? RefundPercent(DateTimeOffset nowUtc, DateTimeOffset departureUtc)
    {
        var remaining = departureUtc - nowUtc;
        if (remaining < TimeSpan.FromHours(24)) return null;
        if (remaining >= TimeSpan.FromDays(7)) return AtLeastSevenDaysPercent;
        if (remaining >= TimeSpan.FromDays(3)) return ThreeToSevenDaysPercent;
        return OneToThreeDaysPercent;
    }
}
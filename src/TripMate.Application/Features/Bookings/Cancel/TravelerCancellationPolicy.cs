using System.Text.Json;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.Cancel;

// Version 1 (legacy snapshots, no "version" property): buckets >=7 days, 3-7 days, 1-3 days; under 24 hours is blocked.
// Version 2 (BR-135): buckets >14 days, 7-14 days, 72 hours-7 days; under 72 hours refunds 0%.
// The three percent fields keep their names in both versions so existing snapshots still deserialize.
public sealed record TravelerCancellationPolicy(decimal AtLeastSevenDaysPercent,
    decimal ThreeToSevenDaysPercent, decimal OneToThreeDaysPercent, int Version = 1)
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
                && policy.Version is 1 or 2
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
        return Version >= 2 ? RefundPercentV2(remaining) : RefundPercentV1(remaining);
    }

    private decimal RefundPercentV2(TimeSpan remaining)
    {
        if (remaining > TimeSpan.FromDays(14)) return AtLeastSevenDaysPercent;
        if (remaining >= TimeSpan.FromDays(7)) return ThreeToSevenDaysPercent;
        if (remaining >= TimeSpan.FromHours(72)) return OneToThreeDaysPercent;
        return 0m;
    }

    private decimal? RefundPercentV1(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromHours(24)) return null;
        if (remaining >= TimeSpan.FromDays(7)) return AtLeastSevenDaysPercent;
        if (remaining >= TimeSpan.FromDays(3)) return ThreeToSevenDaysPercent;
        return OneToThreeDaysPercent;
    }
}
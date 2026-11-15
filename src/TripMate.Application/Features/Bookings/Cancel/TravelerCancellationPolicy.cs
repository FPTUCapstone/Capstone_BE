using System.Text.Json;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.Cancel;

// Version 1 (legacy snapshots, no "version" property): buckets >=7 days, 3-7 days, 1-3 days; under 24 hours is blocked.
// Version 2 (retired BR-135, superseded 11/11/2026): buckets >14 days, 7-14 days, 72 hours-7 days; under 72 hours refunds 0%.
// Version 3 (BR-135, owner decision 11/11/2026): buckets >14 days 100%, 10-14 days 50%, under 10 days 0% (MSG170
// confirmation required, never blocked). Applies only to bookings created from the cutover date onward (Q1); existing
// bookings keep reading their own stored v1/v2 snapshot (BR-107).
// The three percent fields keep their names across versions so existing snapshots still deserialize; v3 ignores
// OneToThreeDaysPercent since its under-10-days tier is fixed at 0% by BR-135, not configurable.
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
                && policy.Version is 1 or 2 or 3
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
        return Version switch
        {
            >= 3 => RefundPercentV3(remaining),
            2 => RefundPercentV2(remaining),
            _ => RefundPercentV1(remaining),
        };
    }

    private decimal RefundPercentV3(TimeSpan remaining)
    {
        if (remaining > TimeSpan.FromDays(14)) return AtLeastSevenDaysPercent;
        if (remaining >= TimeSpan.FromDays(10)) return ThreeToSevenDaysPercent;
        return 0m;
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
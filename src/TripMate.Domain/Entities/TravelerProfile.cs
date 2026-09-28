using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>Read model for dbo.TravelerProfiles preferences used by itinerary planning.</summary>
public sealed class TravelerProfile
{
    private TravelerProfile()
    {
    }

    public long UserId { get; private set; }

    public string? InterestTagsJson { get; private set; }

    /// <summary>
    /// Persisted profile value for future Traveler Preference UC/mobile request prefill;
    /// TM-213 does not use it as a server-side scheduling fallback.
    /// </summary>
    public TransportMode? PreferredTransportMode { get; private set; }

    /// <summary>Persisted for schema parity; no scheduling behavior in TM-213.</summary>
    public TravelerPace? TravelPace { get; private set; }

    /// <summary>
    /// Persisted for schema parity; represents the traveler's willingness to accept risk,
    /// not actual environmental or safety risk, and has no scheduling behavior in TM-213.
    /// </summary>
    public RiskToleranceLevel? RiskTolerance { get; private set; }

    /// <summary>Persisted for schema parity; no scheduling behavior in TM-213.</summary>
    public string? FoodPreferencesJson { get; private set; }

    /// <summary>
    /// Persisted profile value for future Traveler Preference UC/mobile request prefill;
    /// TM-213 does not use it as a server-side scheduling fallback.
    /// </summary>
    public decimal? DefaultBudget { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static TravelerProfile Create(
        long userId,
        string? interestTagsJson,
        DateTimeOffset updatedAtUtc)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        return new TravelerProfile
        {
            UserId = userId,
            InterestTagsJson = interestTagsJson,
            UpdatedAtUtc = updatedAtUtc.ToUniversalTime(),
        };
    }
}

using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>Read model for dbo.TravelerProfiles preferences used by itinerary planning.</summary>
public sealed class TravelerProfile
{
    /// <summary>Column length of interest_tags_json and food_preferences_json.</summary>
    public const int PreferenceJsonMaxLength = 1000;

    private TravelerProfile()
    {
    }

    public long UserId { get; private set; }

    public string? InterestTagsJson { get; private set; }

    /// <summary>Optional personal date of birth used by UC-08 profile maintenance.</summary>
    public DateOnly? DateOfBirth { get; private set; }

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

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static TravelerProfile Create(
        long userId,
        string? interestTagsJson,
        DateTimeOffset updatedAtUtc)
    {
        EnsureUserId(userId);

        return new TravelerProfile
        {
            UserId = userId,
            InterestTagsJson = interestTagsJson,
            UpdatedAtUtc = updatedAtUtc.ToUniversalTime(),
        };
    }

    public void UpdateDateOfBirth(DateOnly? dateOfBirth, DateTimeOffset updatedAtUtc)
    {
        DateOfBirth = dateOfBirth;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    /// <summary>Creates a profile row whose preferences are all unspecified.</summary>
    public static TravelerProfile CreateEmpty(long userId, DateTimeOffset nowUtc)
    {
        EnsureUserId(userId);

        return new TravelerProfile
        {
            UserId = userId,
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    /// <summary>Replaces all five UC-09 preference dimensions at once.</summary>
    public void ReplacePreferences(
        string? interestTagsJson,
        TransportMode? preferredTransportMode,
        TravelerPace? travelPace,
        RiskToleranceLevel? riskTolerance,
        string? foodPreferencesJson,
        DateTimeOffset nowUtc)
    {
        InterestTagsJson = NormalizeJson(interestTagsJson, nameof(interestTagsJson));
        PreferredTransportMode = preferredTransportMode;
        TravelPace = travelPace;
        RiskTolerance = riskTolerance;
        FoodPreferencesJson = NormalizeJson(foodPreferencesJson, nameof(foodPreferencesJson));
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    private static string? NormalizeJson(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > PreferenceJsonMaxLength)
        {
            throw new ArgumentException($"Value cannot exceed {PreferenceJsonMaxLength} characters.", parameterName);
        }

        return value;
    }

    private static void EnsureUserId(long userId)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }
    }
}
namespace TripMate.Domain.Entities;

/// <summary>Read model for dbo.TravelerProfiles preferences used by itinerary planning.</summary>
public sealed class TravelerProfile
{
    private TravelerProfile()
    {
    }

    public long UserId { get; private set; }

    public string? InterestTagsJson { get; private set; }

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

using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// An append-only record of a traveler's permission to disclose their trip location
/// to a specific audience. A consent is effective only while <see cref="RevokedAtUtc"/> is null.
/// </summary>
public sealed class TripLocationDisclosureConsent
{
    private TripLocationDisclosureConsent()
    {
    }

    public long Id { get; private set; }

    public long SessionId { get; private set; }

    public TripSession Session { get; private set; } = null!;

    public long UserId { get; private set; }

    public User User { get; private set; } = null!;

    public LocationDisclosureAudience Audience { get; private set; }

    public DateTimeOffset GrantedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }
}
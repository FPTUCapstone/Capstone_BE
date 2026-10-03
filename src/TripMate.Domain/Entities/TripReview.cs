using System.Globalization;

using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Canonical parent only. The application must supply authorized booking/subject
/// context and policy-accepted, already-normalized text before calling this entity.
/// This entity does not certify moderation, completion or C4 eligibility.
/// </summary>
public sealed class TripReview : BaseEntity
{
    public const int TitleMaxLength = 200;
    public const int ContentMaxLength = 1000;
    public const int EditWindowDays = 7;
    public const string PublishedStatus = "Published";
    public const string NeutralDisplayName = "Traveler";

    private TripReview() { }

    public long BookingId { get; private set; }
    public long TravelerUserId { get; private set; }
    public long? TourId { get; private set; }
    public long? ItineraryId { get; private set; }
    public byte OverallRating { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public RoutePacingFeedback? RoutePacing { get; private set; }
    public byte? CspRating { get; private set; }
    public bool PublishDisplayName { get; private set; }
    public string PublicDisplayName { get; private set; } = string.Empty;
    public string PublicationStatus { get; private set; } = PublishedStatus;
    public string PolicyVersion { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset EditDeadlineUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] Version { get; private set; } = [];

    public static TripReview CreatePublished(long bookingId, long travelerUserId,
        long? tourId, long? itineraryId, byte overallRating, string title, string content,
        RoutePacingFeedback? routePacing, byte? cspRating, bool publishDisplayName,
        string? currentProfileName, string acceptedPolicyVersion, DateTimeOffset createdAtUtc)
    {
        if (bookingId <= 0) throw new ArgumentOutOfRangeException(nameof(bookingId));
        if (travelerUserId <= 0) throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        if (tourId.HasValue == itineraryId.HasValue || tourId is <= 0 || itineraryId is <= 0)
            throw new ArgumentException("Exactly one positive Tour or Itinerary subject is required.");
        ValidatePayload(overallRating, title, content, acceptedPolicyVersion);
        if (routePacing.HasValue && !Enum.IsDefined(routePacing.Value))
            throw new ArgumentOutOfRangeException(nameof(routePacing));
        if (cspRating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(cspRating));
        var created = createdAtUtc.ToUniversalTime();
        return new TripReview
        {
            BookingId = bookingId,
            TravelerUserId = travelerUserId,
            TourId = tourId,
            ItineraryId = itineraryId,
            OverallRating = overallRating,
            Title = title,
            Content = content,
            RoutePacing = routePacing,
            CspRating = cspRating,
            PublishDisplayName = publishDisplayName,
            PublicDisplayName = SnapshotDisplayName(currentProfileName, publishDisplayName),
            PolicyVersion = acceptedPolicyVersion,
            CreatedAtUtc = created,
            EditDeadlineUtc = created.AddDays(EditWindowDays),
            UpdatedAtUtc = created,
        };
    }

    /// <summary>
    /// Receives policy-accepted text without changing it. False means the original
    /// edit window has expired; ownership/version/transaction checks remain application duties.
    /// </summary>
    public bool TryEditPublished(byte overallRating, string title, string content,
        bool publishDisplayName, string? currentProfileName, string acceptedPolicyVersion,
        DateTimeOffset nowUtc)
    {
        if (nowUtc >= EditDeadlineUtc) return false;
        ValidatePayload(overallRating, title, content, acceptedPolicyVersion);
        var snapshot = publishDisplayName == PublishDisplayName
            ? PublicDisplayName : SnapshotDisplayName(currentProfileName, publishDisplayName);
        OverallRating = overallRating;
        Title = title;
        Content = content;
        PublishDisplayName = publishDisplayName;
        PublicDisplayName = snapshot;
        PolicyVersion = acceptedPolicyVersion;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
        return true;
    }

    private static void ValidatePayload(byte rating, string title, string content, string policy)
    {
        if (rating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));
        ValidateNormalizedText(title, TitleMaxLength, nameof(title));
        ValidateNormalizedText(content, ContentMaxLength, nameof(content));
        if (string.IsNullOrWhiteSpace(policy))
            throw new ArgumentException("An accepted policy version is required.", nameof(policy));
    }

    private static void ValidateNormalizedText(string text, int maxLength, string parameter)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength || text != text.Trim())
            throw new ArgumentException("Policy-accepted text must already be normalized and within its bounds.", parameter);
    }

    private static string SnapshotDisplayName(string? name, bool consent)
    {
        if (string.IsNullOrWhiteSpace(name)) return NeutralDisplayName;
        if (consent) return name;
        return string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => StringInfo.GetNextTextElement(token) + "."));
    }
}
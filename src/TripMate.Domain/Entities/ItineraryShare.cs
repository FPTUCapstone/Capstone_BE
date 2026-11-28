namespace TripMate.Domain.Entities;

/// <summary>An immutable, privacy-filtered snapshot and its original chat message receipt.</summary>
public sealed class ItineraryShare
{
    public const string TravelGroup = "travel_group";
    public const string ChatGroup = "chat_group";
    public const string Direct = "direct";
    public const string MessagePrefix = "tripmate:itinerary-share:";
    public const string RetryIndex = "UX_ItineraryShares_ActorClientRequest";
    public const string DefaultTitle = "Shared itinerary";

    private ItineraryShare() { }

    public ItineraryShare(Guid shareId, long actorId, Guid clientRequestId, long itineraryId,
        string conversationKind, long conversationId, long messageId, string title,
        DateTimeOffset sharedAtUtc, int itineraryVersion, string itemsJson)
    {
        if (shareId == Guid.Empty || clientRequestId == Guid.Empty || actorId <= 0 || itineraryId <= 0
            || conversationId <= 0 || messageId <= 0 || itineraryVersion <= 0)
            throw new ArgumentException("A share requires valid identifiers and an itinerary version.");
        if (conversationKind is not (TravelGroup or ChatGroup or Direct))
            throw new ArgumentException("Unknown conversation kind.", nameof(conversationKind));
        ArgumentException.ThrowIfNullOrWhiteSpace(itemsJson);
        ShareId = shareId;
        ActorId = actorId;
        ClientRequestId = clientRequestId;
        ItineraryId = itineraryId;
        ConversationKind = conversationKind;
        ConversationId = conversationId;
        MessageId = messageId;
        Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title.Trim();
        if (Title.Length > Itinerary.TitleMaxLength) throw new ArgumentException("Title is too long.", nameof(title));
        SharedAtUtc = sharedAtUtc.ToUniversalTime();
        ItineraryVersion = itineraryVersion;
        ItemsJson = itemsJson;
    }

    public Guid ShareId { get; private set; }
    public long ActorId { get; private set; }
    public Guid ClientRequestId { get; private set; }
    public long ItineraryId { get; private set; }
    public string ConversationKind { get; private set; } = string.Empty;
    public long ConversationId { get; private set; }
    public long MessageId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public DateTimeOffset SharedAtUtc { get; private set; }
    public int ItineraryVersion { get; private set; }
    public string ItemsJson { get; private set; } = string.Empty;
}
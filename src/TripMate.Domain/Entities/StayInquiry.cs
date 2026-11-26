using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class StayInquiry : BaseEntity
{
    public const string Sent = "Sent";
    public const string Replied = "Replied";
    public const string Declined = "Declined";

    private StayInquiry() { }

    public StayInquiry(long poiId, long travelerId, long? itineraryId, DateOnly arrival, DateOnly departure,
        int guests, string contactName, string contact, string? note, Guid submitKey, string submitHash, DateTimeOffset createdAtUtc)
    {
        if (poiId <= 0 || travelerId <= 0 || itineraryId <= 0 || departure <= arrival || guests <= 0
            || string.IsNullOrWhiteSpace(contactName) || string.IsNullOrWhiteSpace(contact) || submitKey == Guid.Empty)
            throw new ArgumentException("Invalid stay inquiry.");

        PoiId = poiId;
        TravelerId = travelerId;
        ItineraryId = itineraryId;
        Arrival = arrival;
        Departure = departure;
        Guests = guests;
        ContactName = contactName;
        Contact = contact;
        Note = note;
        SubmitKey = submitKey;
        SubmitHash = submitHash;
        CreatedAtUtc = createdAtUtc;
    }

    public long PoiId { get; private set; }
    public long TravelerId { get; private set; }
    public long? ItineraryId { get; private set; }
    public DateOnly Arrival { get; private set; }
    public DateOnly Departure { get; private set; }
    public int Guests { get; private set; }
    public string ContactName { get; private set; } = string.Empty;
    public string Contact { get; private set; } = string.Empty;
    public string? Note { get; private set; }
    public Guid SubmitKey { get; private set; }
    public string SubmitHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string Status { get; private set; } = Sent;
    public string? Reply { get; private set; }

    public bool TryReply(bool declined, string reply)
    {
        if (Status != Sent || string.IsNullOrWhiteSpace(reply)) return false;
        Status = declined ? Declined : Replied;
        Reply = reply.Trim();
        return true;
    }
}

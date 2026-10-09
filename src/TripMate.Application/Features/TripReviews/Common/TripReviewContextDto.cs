using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripMate.Application.Features.TripReviews.Common;

public sealed record TripReviewContextDto([property: JsonIgnore] long ParentId, string BookingStatus,
    TripReviewSubjectDto? Subject,
    bool CanSubmit, string? SubmitUnavailableReason, string ExistingReviewKind, TripReviewReadDto? Review,
    TripReviewCapabilityDto RoutePacing, TripReviewCapabilityDto CspRating, TripReviewCapabilityDto PoiRatings,
    IReadOnlyList<EligibleReviewPoiDto> EligiblePois, IReadOnlyList<string> EditableFields,
    TripReviewDisplayNamePreviewDto DisplayNamePreview, ReviewableRecordRef? ReviewableRecord = null,
    TripReviewSummaryDto? Summary = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BookingId => ReviewableRecord is null || ReviewableRecord.IsCommerceBooking
        ? ParentId
        : null;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ServiceBookingId => ReviewableRecord?.IsServiceBooking == true
        ? ParentId
        : null;
}

public sealed record TripReviewSubjectDto(string Kind, long Id)
{
    public TripReviewSubjectDto(string kind, long? tourId, long? itineraryId)
        : this(kind, tourId ?? itineraryId
            ?? throw new ArgumentException("A canonical subject ID is required."))
    { }

    [JsonIgnore]
    public long? TourId => Kind == TripReviewContextValues.TourSubject ? Id : null;

    [JsonIgnore]
    public long? ItineraryId => Kind == TripReviewContextValues.ItinerarySubject ? Id : null;

    [JsonIgnore]
    public long? PoiId => Kind == TripReviewContextValues.PoiSubject ? Id : null;
}
public sealed record TripReviewSummaryDto(string Name,
    [property: JsonConverter(typeof(TripReviewUtcDateTimeOffsetJsonConverter))] DateTimeOffset DepartureAtUtc,
    string BookingReference);
public sealed record TripReviewCapabilityDto(bool Available, string? Reason);
public sealed record EligibleReviewPoiDto(long PoiId, string Name);
public sealed record TripReviewDisplayNamePreviewDto(string Initials, string? FullNameWithConsent);

[JsonDerivedType(typeof(NewTripReviewDto))]
[JsonDerivedType(typeof(LegacyTripReviewDto))]
public abstract record TripReviewReadDto(string Kind);

public sealed record NewTripReviewDto(long ReviewId, [property: JsonIgnore] long ParentId,
    TripReviewSubjectDto Subject,
    byte OverallRating, string Title, string Content, string? RoutePacing, byte? CspRating,
    bool PublishDisplayName, string PublicDisplayName, string PublicationStatus,
    [property: JsonConverter(typeof(TripReviewUtcDateTimeOffsetJsonConverter))] DateTimeOffset CreatedAtUtc,
    [property: JsonConverter(typeof(TripReviewUtcDateTimeOffsetJsonConverter))] DateTimeOffset EditDeadlineUtc,
    [property: JsonConverter(typeof(TripReviewUtcDateTimeOffsetJsonConverter))] DateTimeOffset UpdatedAtUtc,
    string Version,
    IReadOnlyList<ReviewPoiRatingDto> PoiRatings, IReadOnlyList<TripReviewMediaDto> Media,
    ReviewableRecordRef? ReviewableRecord = null) : TripReviewReadDto(TripReviewContextValues.New)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BookingId => ReviewableRecord is null || ReviewableRecord.IsCommerceBooking
        ? ParentId
        : null;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ServiceBookingId => ReviewableRecord?.IsServiceBooking == true
        ? ParentId
        : null;
}

public sealed record LegacyTripReviewDto(long BookingId, IReadOnlyList<LegacyTripReviewEntryDto> Entries) : TripReviewReadDto(TripReviewContextValues.Legacy);
public sealed record LegacyTripReviewEntryDto(long ReviewId, string TargetType, long TargetId, byte Rating, string? Comment,
    [property: JsonConverter(typeof(TripReviewUtcDateTimeOffsetJsonConverter))] DateTimeOffset CreatedAtUtc);
public sealed record ReviewPoiRatingDto(long PoiId, byte Rating);
public sealed record TripReviewMediaDto(long MediaId, string DeliveryUrl);

public sealed class TripReviewUtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset value,
        JsonSerializerOptions options) => writer.WriteStringValue(value.UtcDateTime);
}

public static class TripReviewContextValues
{
    public const string Completed = "Completed";
    public const string None = "none";
    public const string New = "new";
    public const string Legacy = "legacy";
    public const string TourSubject = "tour";
    public const string ItinerarySubject = "itinerary";
    public const string PoiSubject = "poi";
    public const string Published = "published";
    public const string InconsistentContext = "inconsistentContext";
    public const string LegacyConflict = "legacyConflict";
    public const string AlreadyReviewed = "alreadyReviewed";
    public const string BookingNotCompleted = "bookingNotCompleted";
    public const string UnsupportedSubject = "unsupportedSubject";
    public const string RouteContextUnavailable = "routeContextUnavailable";
    public const string VisitEvidenceUnavailable = "visitEvidenceUnavailable";
    public const string CspProvenanceUnavailable = "cspProvenanceUnavailable";
    public const string TooTight = "tooTight";
    public const string WellPaced = "wellPaced";
    public const string TooLoose = "tooLoose";
}
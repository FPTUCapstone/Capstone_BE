using System.Text.Json.Serialization;

namespace TripMate.Application.Features.TripReviews.Common;

/// <summary>
/// Namespace-safe identity for a record that may own one canonical trip review.
/// Numeric IDs are meaningful only together with their kind.
/// </summary>
public sealed record ReviewableRecordRef
{
    public const string CommerceBookingKind = "commerceBooking";
    public const string ServiceBookingKind = "serviceBooking";

    private ReviewableRecordRef(string kind, long id, bool requirePositive = true)
    {
        if (requirePositive && id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "A reviewable record ID must be positive.");
        if (kind is not (CommerceBookingKind or ServiceBookingKind))
            throw new ArgumentOutOfRangeException(nameof(kind), "The reviewable record kind is not supported.");

        Kind = kind;
        Id = id;
    }

    public string Kind { get; }
    public long Id { get; }

    [JsonIgnore]
    public string LockKey => Kind switch
    {
        CommerceBookingKind => $"commerce-booking:{Id}",
        ServiceBookingKind => $"service-booking:{Id}",
        _ => throw new InvalidOperationException("The reviewable record kind is not supported."),
    };

    [JsonIgnore]
    public bool IsCommerceBooking => Kind == CommerceBookingKind;

    [JsonIgnore]
    public bool IsServiceBooking => Kind == ServiceBookingKind;

    public static ReviewableRecordRef CommerceBooking(long id) => new(CommerceBookingKind, id);
    public static ReviewableRecordRef ServiceBooking(long id) => new(ServiceBookingKind, id);

    // Compatibility constructors allow FluentValidation to produce the existing
    // stable invalid-input result instead of turning malformed legacy input into
    // an exception. Public canonical factories remain positive-only.
    internal static ReviewableRecordRef UnvalidatedCommerceBooking(long id) =>
        new(CommerceBookingKind, id, requirePositive: false);
}
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>Offered actions of one record (spec TM-78 P6, P7, P8).</summary>
public sealed record TripActionsResponse(
    bool CanReview,
    bool CanViewETicket,
    long? TicketId,
    bool CanViewRefund);

/// <summary>Existing-review state of a booking; <c>reviewId</c> and <c>rating</c> only when a review exists.</summary>
public sealed record TripReviewStateResponse(
    bool Exists,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ReviewId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Rating);

/// <summary>
/// One Trip History record. Booking-only and itinerary-only members are omitted from the JSON of the
/// other kind; <c>reviewableRecord</c> and <c>review</c> are always written (null for itineraries).
/// </summary>
public record TripHistoryItemResponse
{
    public required string Kind { get; init; }
    public required long Id { get; init; }
    public required string TripType { get; init; }
    public required string Tab { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset DepartureAtUtc { get; init; }
    public required string Status { get; init; }
    public required ReviewableRecordRef? ReviewableRecord { get; init; }
    public required TripActionsResponse Actions { get; init; }
    public required TripReviewStateResponse? Review { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BookingCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ParticipantCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? TotalAmount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StopCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VisitedCount { get; init; }
}

/// <summary>Completed-tab statistics under the active filters (P9).</summary>
public sealed record TripHistorySummaryResponse(int TotalCompletedTrips, int TotalVisitedPois);

public sealed record TripHistoryPage(
    IReadOnlyCollection<TripHistoryItemResponse> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TripHistorySummaryResponse? Summary,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record TripTourInfo(string Title, string DestinationSummary);

public sealed record TripScheduleInfo(DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc, string? MeetingPoint);

public sealed record TripParticipantCounts(int Adult, int Child, int Infant);

public sealed record TripCancellationInfo(DateTimeOffset? CancelledAtUtc, string? Reason);

/// <summary>Latest refund of a cancelled booking (T3); <c>channel</c> is the original payment gateway.</summary>
public sealed record TripRefundInfo(string Status, decimal Amount, string? Channel);

public sealed record TripStopResponse(
    int SequenceNo,
    long PoiId,
    string PoiName,
    DateTimeOffset? PlannedArrivalUtc,
    DateTimeOffset? PlannedDepartureUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VisitStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ReachedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? SkippedAtUtc);

public sealed record TripSessionInfo(DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc, string? CompletionReason);

/// <summary>The list item fields plus the kind-specific detail (spec TM-78 §7.2).</summary>
public sealed record TripDetailResponse : TripHistoryItemResponse
{
    /// <summary>Starts from an already built card so the list and the detail can never disagree.</summary>
    [SetsRequiredMembers]
    public TripDetailResponse(TripHistoryItemResponse card)
        : base(card)
    {
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripTourInfo? Tour { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripScheduleInfo? Schedule { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripParticipantCounts? Participants { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripCancellationInfo? Cancellation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripRefundInfo? Refund { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ValidFromUtc { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ValidToUtc { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyCollection<TripStopResponse>? Stops { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TripSessionInfo? Session { get; init; }
}
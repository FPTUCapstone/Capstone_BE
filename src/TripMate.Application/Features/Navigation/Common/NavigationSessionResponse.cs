using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Navigation.Common;

public sealed record NavigationSessionResponse(
    long SessionId,
    long ItineraryId,
    int ItineraryVersion,
    string State,
    string? CompletionReason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? EndedAtUtc,
    long? ExploringItemId,
    long? NextItemId,
    IReadOnlyCollection<NavigationSessionItemResponse> Items,
    TransportMode? TransportMode)
{
    /// <summary>
    /// Builds the response; the itinerary's scheduling request (when loaded) supplies the planned
    /// transport mode so the client routes for the same mode the CSP scheduled. Manual itineraries
    /// have none. The itinerary's items (when loaded) supply each stop's own leg mode, which the
    /// traveler may have changed per leg; the client falls back to the trip mode when it is null.
    /// </summary>
    public static NavigationSessionResponse From(TripSession session, Itinerary itinerary) =>
        Build(
            session,
            itinerary.Version,
            itinerary.SchedulingRequest?.TransportMode,
            TransportModesFromPrevious(itinerary));

    public static NavigationSessionResponse From(
        TripSession session,
        int itineraryVersion,
        TransportMode? transportMode = null) =>
        Build(session, itineraryVersion, transportMode, transportModesFromPrevious: null);

    private static NavigationSessionResponse Build(
        TripSession session,
        int itineraryVersion,
        TransportMode? transportMode,
        IReadOnlyDictionary<long, TransportMode?>? transportModesFromPrevious)
    {
        var items = session.Items
            .OrderBy(item => item.SequenceNo)
            .Select(item => new NavigationSessionItemResponse(
                item.ItineraryItemId,
                item.SequenceNo,
                item.PoiId,
                item.PoiName,
                item.Latitude,
                item.Longitude,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                item.IsMandatory,
                item.Status,
                item.ReachedAtUtc,
                item.SkippedAtUtc,
                transportModesFromPrevious?.GetValueOrDefault(item.ItineraryItemId)))
            .ToArray();
        return new NavigationSessionResponse(
            session.Id,
            session.ItineraryId,
            itineraryVersion,
            session.FsmState,
            session.CompletionReason,
            session.StartedAtUtc!.Value,
            session.ExpiresAtUtc,
            session.EndedAtUtc,
            session.ExploringItemId,
            SuggestNextItemId(session.FsmState, items),
            items,
            transportMode);
    }

    /// <summary>
    /// The mode of the leg that reaches each itinerary item: the <c>TransportModeToNext</c> of the
    /// item just before it. That item may be a rest, whose mode is the second half of a leg split
    /// by the rest. The first item has no incoming leg.
    /// </summary>
    private static Dictionary<long, TransportMode?> TransportModesFromPrevious(Itinerary itinerary)
    {
        var ordered = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        var modes = new Dictionary<long, TransportMode?>();
        for (var index = 1; index < ordered.Length; index++)
        {
            modes[ordered[index].Id] = ordered[index - 1].TransportModeToNext;
        }

        return modes;
    }

    /// <summary>
    /// The advisory next stop: the lowest-sequence Pending item of an open session. It is null
    /// for a Completed session and when only Skipped items remain; a Skipped item can still be
    /// reached on request but is never suggested again.
    /// </summary>
    private static long? SuggestNextItemId(
        string state,
        IEnumerable<NavigationSessionItemResponse> orderedItems) =>
        state == TripSession.CompletedState
            ? null
            : orderedItems.FirstOrDefault(item => item.Status == TripSessionItem.PendingStatus)?.ItemId;
}

public sealed record NavigationSessionItemResponse(
    long ItemId,
    int SequenceNo,
    long PoiId,
    string PoiName,
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset PlannedDepartureUtc,
    bool IsMandatory,
    string Status,
    DateTimeOffset? ReachedAtUtc,
    DateTimeOffset? SkippedAtUtc,
    TransportMode? TransportModeFromPrevious);
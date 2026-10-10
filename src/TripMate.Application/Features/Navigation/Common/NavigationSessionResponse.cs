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
    /// have none.
    /// </summary>
    public static NavigationSessionResponse From(TripSession session, Itinerary itinerary) =>
        From(session, itinerary.Version, itinerary.SchedulingRequest?.TransportMode);

    public static NavigationSessionResponse From(
        TripSession session,
        int itineraryVersion,
        TransportMode? transportMode = null)
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
                item.SkippedAtUtc))
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
            items.FirstOrDefault(item => item.Status == TripSessionItem.PendingStatus)?.ItemId,
            items,
            transportMode);
    }
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
    DateTimeOffset? SkippedAtUtc);
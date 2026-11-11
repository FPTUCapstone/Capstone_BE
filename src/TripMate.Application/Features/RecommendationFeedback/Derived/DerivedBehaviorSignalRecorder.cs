using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.RecommendationFeedback.Derived;

/// <summary>
/// UC-10 item 6 (spec .scratch/uc10-behavior-learning, D1/D6/D7): appends behavior events derived from what the
/// traveler does, so the ranking can learn without a feedback button. Each <c>Record*</c> call runs inside the
/// caller's transaction after the caller saved its own change, and saves the events on its own: a failed insert
/// (for example the CHECK constraints before the migration is applied) is logged and detached, never surfaced to
/// the traveler. <see cref="ClientEventIdFor"/> makes every signal idempotent per action.
/// </summary>
internal sealed class DerivedBehaviorSignalRecorder(IApplicationDbContext dbContext, ILogger? logger = null)
{
    /// <summary>
    /// Adjust: a visit present in the source version but not in the new stops is a Dislike; a new stop is a Like.
    /// A reorder of the same stops records nothing.
    /// </summary>
    public async Task<int> RecordItineraryEditAsync(
        long travelerUserId,
        long sourceItineraryId,
        IEnumerable<long> newVisitPoiIds,
        Guid operationKey,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        // Visits only: the system places rest stops itself (sometimes at a POI) and recomputes them on every
        // adjust, so a rest stop that disappears is not something the traveler removed.
        var sourceVisits = await dbContext.ItineraryItems
            .AsNoTracking()
            .Where(item => item.ItineraryId == sourceItineraryId
                && item.Kind == ItineraryItemKind.Visit
                && item.PointOfInterestId != null)
            .Select(item => new { PoiId = item.PointOfInterestId!.Value, item.IsMandatory })
            .ToListAsync(cancellationToken);
        var sourceIds = sourceVisits.Select(visit => visit.PoiId).ToHashSet();
        var newIds = newVisitPoiIds.ToHashSet();
        var key = operationKey.ToString("N");

        var events = new List<RecommendationBehaviorEvent>();
        foreach (var removed in sourceVisits.Where(visit => !newIds.Contains(visit.PoiId)))
        {
            events.Add(Event(
                travelerUserId, removed.PoiId, sourceItineraryId, RecommendationEventType.Dislike,
                originalPosition: null, removed.IsMandatory, RecommendationCaptureSource.ItineraryEdit,
                occurredAtUtc, key));
        }

        foreach (var addedPoiId in newIds.Where(poiId => !sourceIds.Contains(poiId)))
        {
            events.Add(Event(
                travelerUserId, addedPoiId, sourceItineraryId, RecommendationEventType.Like,
                originalPosition: null, wasMandatory: false, RecommendationCaptureSource.ItineraryEdit,
                occurredAtUtc, key));
        }

        return await AppendAsync(travelerUserId, events, cancellationToken);
    }

    /// <summary>
    /// Navigation: a reached visit is a Like, a skipped visit is a Skip at its position in the route. A rest
    /// stop says nothing about taste, even when it is at a POI.
    /// </summary>
    public async Task<int> RecordNavigationAsync(
        TripSession session,
        long itineraryItemId,
        RecommendationEventType eventType,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var item = session.Items.FirstOrDefault(candidate => candidate.ItineraryItemId == itineraryItemId);
        if (item is null)
        {
            return 0;
        }

        bool isVisit = await dbContext.ItineraryItems
            .AsNoTracking()
            .AnyAsync(
                candidate => candidate.Id == itineraryItemId && candidate.Kind == ItineraryItemKind.Visit,
                cancellationToken);
        if (!isVisit)
        {
            return 0;
        }

        var signal = Event(
            session.TravelerUserId, item.PoiId, session.ItineraryId, eventType,
            eventType == RecommendationEventType.Skip ? item.SequenceNo : null, item.IsMandatory,
            RecommendationCaptureSource.Navigation, occurredAtUtc, $"{session.Id}:{itineraryItemId}");
        return await AppendAsync(session.TravelerUserId, [signal], cancellationToken);
    }

    /// <summary>Create: every POI the traveler marked mandatory is a Like with no itinerary yet.</summary>
    public Task<int> RecordMandatoryAsync(
        long travelerUserId,
        long schedulingRequestId,
        IEnumerable<long> mandatoryPoiIds,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var events = mandatoryPoiIds
            .Distinct()
            .Select(poiId => Event(
                travelerUserId, poiId, itineraryId: null, RecommendationEventType.Like,
                originalPosition: null, wasMandatory: true, RecommendationCaptureSource.SchedulingRequest,
                occurredAtUtc, schedulingRequestId.ToString()))
            .ToList();
        return AppendAsync(travelerUserId, events, cancellationToken);
    }

    /// <summary>Stable id per (source, action, POI, type), so a retried action never writes a second signal.</summary>
    internal static Guid ClientEventIdFor(
        RecommendationCaptureSource source,
        string operationKey,
        long poiId,
        RecommendationEventType eventType)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{source}:{operationKey}:{poiId}:{eventType}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static RecommendationBehaviorEvent Event(
        long travelerUserId,
        long poiId,
        long? itineraryId,
        RecommendationEventType eventType,
        int? originalPosition,
        bool? wasMandatory,
        RecommendationCaptureSource source,
        DateTimeOffset occurredAtUtc,
        string operationKey) =>
        RecommendationBehaviorEvent.Create(
            travelerUserId,
            poiId,
            itineraryId,
            eventType,
            originalPosition,
            newPosition: null,
            wasMandatory,
            source,
            occurredAtUtc,
            ClientEventIdFor(source, operationKey, poiId, eventType));

    private async Task<int> AppendAsync(
        long travelerUserId,
        IReadOnlyList<RecommendationBehaviorEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return 0;
        }

        var fresh = new List<RecommendationBehaviorEvent>();
        try
        {
            var ids = events.Select(signal => signal.ClientEventId).ToArray();
            var existing = await dbContext.RecommendationBehaviorEvents
                .AsNoTracking()
                .Where(stored => stored.TravelerUserId == travelerUserId && ids.Contains(stored.ClientEventId))
                .Select(stored => stored.ClientEventId)
                .ToListAsync(cancellationToken);
            fresh.AddRange(events.Where(signal => !existing.Contains(signal.ClientEventId)));
            if (fresh.Count == 0)
            {
                return 0;
            }

            dbContext.RecommendationBehaviorEvents.AddRange(fresh);
            await dbContext.SaveChangesAsync(cancellationToken);
            return fresh.Count;
        }
        catch (Exception exception) when (IsStorageFailure(exception) && !dbContext.IsDeadlock(exception))
        {
            // A deadlock is rethrown: it rolled the transaction back, and the caller's retry handles it.
            // Removing an Added entity detaches it, so the caller's later saves do not retry the failed rows.
            dbContext.RecommendationBehaviorEvents.RemoveRange(fresh);
            logger?.LogWarning(
                exception,
                "derived_behavior_signal_failed traveler={TravelerUserId} source={Source} count={Count}",
                travelerUserId,
                events[0].Source,
                events.Count);
            return 0;
        }
    }

    // The table or its relaxed CHECK constraints may be missing (migration not applied yet), or the context may not
    // expose the set at all: none of these may fail the traveler's action.
    private static bool IsStorageFailure(Exception exception) =>
        exception is DbUpdateException or DbException or NotSupportedException;
}
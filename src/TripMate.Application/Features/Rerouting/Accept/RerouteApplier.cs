using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Rerouting.Accept;

/// <summary>
/// Applies an accepted proposal (spec 6 steps 8-12). It runs inside the accept transaction and uses five explicit
/// saves because SQL Server will not accept the natural single save:
/// <list type="number">
/// <item><description>S1 successor version + the session row-version fence, before any destructive write.</description></item>
/// <item><description>S2 every incident that still points at a Pending row is resolved and its pointer set to NULL (the FK
/// from <c>trip.Incidents</c> to <c>trip.TripSessionItems</c> is NoAction).</description></item>
/// <item><description>S3 the Pending rows are deleted. This must precede S4: the new rows reuse the sequence numbers the old
/// rows hold and <c>UQ_TripSessionItems_Sequence</c> would reject the insert.</description></item>
/// <item><description>S4 the new rows are inserted, the session moves to the new version, sibling proposals expire and this
/// proposal is accepted.</description></item>
/// <item><description>S5 incidents whose POI is still in the plan are pointed at the new row of that POI.</description></item>
/// </list>
/// Any exception rolls the whole transaction back, so the old version, rows and pointers stay in force.
/// </summary>
internal sealed class RerouteApplier(IApplicationDbContext dbContext, IItineraryVersionService versionService)
{
    public async Task<Itinerary> ApplyAsync(
        TripSession session,
        ReroutingEvent proposal,
        SchedulingRequest schedulingRequest,
        GeneratedItineraryPlan plan,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var keptRows = session.Items.Where(item => item.Status != TripSessionItem.PendingStatus).ToArray();
        var pendingRows = session.Items.Where(item => item.Status == TripSessionItem.PendingStatus).ToArray();

        // History keeps its original items (any older version), so they are read by id, not through the current version.
        var keptIds = keptRows.Select(item => item.ItineraryItemId).ToArray();
        var keptItems = await dbContext.ItineraryItems.AsNoTracking()
            .Where(item => keptIds.Contains(item.Id))
            .OrderBy(item => item.SequenceNo)
            .ToListAsync(cancellationToken);
        var highestKept = Math.Max(
            keptRows.Length == 0 ? 0 : keptRows.Max(item => item.SequenceNo),
            keptItems.Count == 0 ? 0 : keptItems.Max(item => item.SequenceNo));
        var firstNewSequence = highestKept + 1;

        var created = await versionService.CreateReroutedVersionAsync(
            session.Itinerary, schedulingRequest, keptItems, plan, firstNewSequence, nowUtc, cancellationToken);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"The rerouted version could not be built: {created.ErrorMessage}");
        }

        var successor = created.Value;

        // S1
        dbContext.MarkTripSessionProgressForConcurrencyCheck(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        // S2
        var pendingItemIds = pendingRows.Select(item => item.ItineraryItemId).ToArray();
        var incidents = await dbContext.Incidents
            .Where(incident => incident.SessionId == session.Id
                && incident.AffectedItineraryItemId != null
                && pendingItemIds.Contains(incident.AffectedItineraryItemId.Value))
            .ToListAsync(cancellationToken);
        var repoints = new List<(Incident Incident, long PoiId)>();
        foreach (var incident in incidents)
        {
            var poiId = pendingRows.Single(item => item.ItineraryItemId == incident.AffectedItineraryItemId).PoiId;
            incident.Resolve(nowUtc);
            incident.DetachAffectedItem();
            repoints.Add((incident, poiId));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // S3
        session.RemovePendingItems();
        await dbContext.SaveChangesAsync(cancellationToken);

        // S4
        var newItems = successor.Items
            .Where(item => item.PointOfInterestId.HasValue && item.SequenceNo >= firstNewSequence)
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        var poiIds = newItems.Select(item => item.PointOfInterestId!.Value).Distinct().ToArray();
        var pois = await dbContext.PointsOfInterest.AsNoTracking()
            .Where(poi => poiIds.Contains(poi.Id))
            .ToDictionaryAsync(poi => poi.Id, cancellationToken);
        var newRows = newItems.Select(item =>
        {
            var poi = pois[item.PointOfInterestId!.Value];
            return TripSessionItem.Snapshot(
                item.Id,
                item.SequenceNo,
                poi.Id,
                poi.Name,
                poi.Latitude,
                poi.Longitude,
                item.PlannedArrivalUtc!.Value,
                item.PlannedDepartureUtc!.Value,
                item.IsMandatory);
        }).ToArray();
        session.AttachRerouteItems(successor.Id, newRows, nowUtc);

        var siblings = await dbContext.ReroutingEvents
            .Where(other => other.SessionId == session.Id
                && other.Id != proposal.Id
                && other.Status == ReroutingEvent.StatusProposed)
            .ToListAsync(cancellationToken);
        foreach (var sibling in siblings)
        {
            sibling.Expire(nowUtc);
        }

        proposal.Accept(nowUtc);
        await dbContext.SaveChangesAsync(cancellationToken);

        // S5
        foreach (var (incident, poiId) in repoints)
        {
            var row = newRows.FirstOrDefault(candidate => candidate.PoiId == poiId);
            if (row is not null)
            {
                incident.RepointAffectedItem(row);
            }
        }

        if (repoints.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return successor;
    }
}
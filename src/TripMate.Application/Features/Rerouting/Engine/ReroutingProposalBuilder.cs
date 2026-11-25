using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.SystemConfigs.Common;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Rerouting.Common;
using TripMate.Application.Features.Rerouting.Engine.Strategies;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Rerouting.Engine;

/// <summary>
/// The Rerouting Engine (UC-15, spec section 4): given an incident, returns the best feasible alternative plan for
/// the remaining stops, or null when there is nothing to propose. It reads and evaluates but never saves, so a proposal
/// can only exist after the generator stores one (BR-33: nothing changes without the Traveler's confirmation).
/// </summary>
internal sealed class ReroutingProposalBuilder(
    IApplicationDbContext dbContext,
    IItineraryVersionService versionService,
    IOptions<ReroutingOptions> options)
{
    private static readonly IReroutingStrategy[] Strategies =
    [
        new SevereWeatherStrategy(),
        new PoiClosureStrategy(),
        new ScheduleDelayStrategy(),
        new RouteDeviationStrategy(),
    ];

    private readonly ReroutingOptions options = options.Value;

    public async Task<ReroutingProposalSnapshot?> BuildAsync(
        long incidentId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var incident = await dbContext.Incidents.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == incidentId, cancellationToken);
        var strategy = incident is null
            ? null
            : Array.Find(Strategies, candidate => candidate.IncidentType == incident.IncidentType);
        if (incident is null || strategy is null || !incident.AffectedItineraryItemId.HasValue)
        {
            return null;
        }

        var session = await dbContext.TripSessions.AsNoTracking()
            .Include(candidate => candidate.Items)
            .Include(candidate => candidate.Itinerary)
            .ThenInclude(itinerary => itinerary.SchedulingRequest)
            .SingleOrDefaultAsync(candidate => candidate.Id == incident.SessionId, cancellationToken);
        var request = session?.Itinerary.SchedulingRequest;
        if (session is null || request is null)
        {
            return null;
        }

        var pending = session.Items
            .Where(item => item.Status == TripSessionItem.PendingStatus)
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        var affected = pending.SingleOrDefault(item => item.ItineraryItemId == incident.AffectedItineraryItemId.Value);
        if (affected is null)
        {
            return null;
        }

        var itemIds = session.Items.Select(item => item.ItineraryItemId).ToArray();
        var storedItems = await dbContext.ItineraryItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) || item.ItineraryId == session.ItineraryId)
            .Select(item => new StoredItem(
                item.Id, item.ItineraryId, item.PointOfInterestId, item.EstimatedCost, item.TravelDurationToNextMinutes))
            .ToArrayAsync(cancellationToken);
        var radius = await ReadSearchRadiusAsync(cancellationToken);
        var anchor = ReroutingAnchorFactory.Create(
            session,
            request,
            affected,
            storedItems.Where(item => itemIds.Contains(item.Id))
                .ToDictionary(item => item.Id, item => item.EstimatedCost),
            radius,
            nowUtc);

        var pendingPoiIds = pending.Select(item => item.PoiId).ToArray();
        var pendingPois = await dbContext.PointsOfInterest.AsNoTracking()
            .Include(poi => poi.Category)
            .Include(poi => poi.OpeningHours)
            .Include(poi => poi.PoiTags)
            .ThenInclude(mapping => mapping.Tag)
            .Where(poi => pendingPoiIds.Contains(poi.Id))
            .ToDictionaryAsync(poi => poi.Id, cancellationToken);
        if (!pendingPois.TryGetValue(affected.PoiId, out var affectedPoi))
        {
            return null;
        }

        var interestTags = await dbContext.TravelerProfiles.AsNoTracking()
            .Where(profile => profile.UserId == session.TravelerUserId)
            .Select(profile => profile.InterestTagsJson)
            .SingleOrDefaultAsync(cancellationToken);
        var tokens = TravelerPreferenceScoring.ParsePreferenceTokens(interestTags);
        var scores = pendingPois.ToDictionary(
            entry => entry.Key,
            entry => TravelerPreferenceScoring.CalculatePreferenceScore(entry.Value, tokens));
        var excluded = session.Items.Select(item => item.PoiId)
            .Concat(storedItems.Where(item => item.ItineraryId == session.ItineraryId && item.PoiId.HasValue)
                .Select(item => item.PoiId!.Value))
            .ToHashSet();

        var context = new ReroutingStrategyContext(
            pending,
            affected,
            affectedPoi,
            anchor,
            options.MaxVariants,
            options.MaxReplacementsPerStop,
            tokens,
            scores,
            excluded,
            new ReroutingCandidateFinder(dbContext));
        var currentOrder = pendingPoiIds;
        var batches = await strategy.CreateBatchesAsync(context, cancellationToken);
        foreach (var batch in batches)
        {
            var variants = batch
                .Where(variant => variant.IsBaseProbe || !variant.Stops.Select(stop => stop.PoiId).SequenceEqual(currentOrder))
                .ToArray();
            if (variants.Length == 0)
            {
                continue;
            }

            var evaluations = await versionService.EvaluateStopVariantsAsync(
                request,
                variants.Select(variant => variant.Stops).ToArray(),
                cancellationToken,
                anchor: anchor);
            if (evaluations.IsFailure)
            {
                return null;
            }

            // The current order already fits the rest of the day: there is nothing to fix (BR-25 is not at risk).
            var probe = Array.FindIndex(variants, variant => variant.IsBaseProbe);
            if (probe >= 0 && evaluations.Value[probe].Plan is not null)
            {
                return null;
            }

            var feasible = variants
                .Select((variant, index) => new Candidate(variant, evaluations.Value[index].Plan, index))
                .Where(candidate => !candidate.Variant.IsBaseProbe && candidate.Plan is not null)
                .Select(candidate => candidate with { Diffs = Differences(candidate.Plan!, pending, storedItems) })
                .OrderBy(candidate => candidate.Diffs!.Arrival)
                .ThenBy(candidate => candidate.Diffs!.Travel)
                .ThenBy(candidate => candidate.Index)
                .FirstOrDefault();
            if (feasible is not null)
            {
                return await ToSnapshotAsync(incident, session, request, pending, anchor, feasible, cancellationToken);
            }
        }

        return null;
    }

    private async Task<decimal> ReadSearchRadiusAsync(CancellationToken cancellationToken)
    {
        var value = await dbContext.SystemConfigs.AsNoTracking()
            .Where(config => config.ConfigKey == AlgorithmParameterDefinitions.ReroutingSearchRadiusKmKey)
            .Select(config => config.ConfigValue)
            .SingleOrDefaultAsync(cancellationToken);
        return (decimal)AlgorithmParameterDefinitions.ParseSearchRadius(value);
    }

    /// <summary>
    /// Spec 3.2: travel is the sum of the legs between the remaining stops (the leg after the last stop is left out of
    /// both plans); arrival is the arrival at the last remaining stop against its planned arrival.
    /// </summary>
    private static PlanDifferences Differences(
        GeneratedItineraryPlan plan,
        IReadOnlyList<TripSessionItem> pending,
        IReadOnlyList<StoredItem> storedItems)
    {
        var proposed = plan.Items.OrderBy(item => item.SequenceNo).ToArray();
        var proposedTravel = proposed.Take(Math.Max(0, proposed.Length - 1))
            .Sum(item => item.TravelDurationToNextMinutes ?? 0);
        var currentTravel = pending.Take(Math.Max(0, pending.Count - 1))
            .Sum(item => storedItems.FirstOrDefault(stored => stored.Id == item.ItineraryItemId)?.TravelMinutes ?? 0);
        var lastVisit = proposed.LastOrDefault(item => item.Kind == ItineraryItemKind.Visit) ?? proposed[^1];
        var arrival = (int)Math.Round((lastVisit.PlannedArrivalUtc - pending[^1].PlannedArrivalUtc).TotalMinutes);
        return new PlanDifferences(proposedTravel - currentTravel, arrival);
    }

    private async Task<ReroutingProposalSnapshot> ToSnapshotAsync(
        Incident incident,
        TripSession session,
        SchedulingRequest request,
        IReadOnlyList<TripSessionItem> pending,
        EvaluationAnchor anchor,
        Candidate winner,
        CancellationToken cancellationToken)
    {
        var plan = winner.Plan!;
        var visits = plan.Items
            .Where(item => item.Kind == ItineraryItemKind.Visit && item.PointOfInterestId.HasValue)
            .OrderBy(item => item.SequenceNo)
            .ToArray();
        var proposedPoiIds = visits.Select(item => item.PointOfInterestId!.Value).ToArray();
        var basePoiIds = pending.Select(item => item.PoiId).ToArray();
        var keptInOrder = LongestCommonSubsequence(basePoiIds, proposedPoiIds);

        var known = pending.ToDictionary(item => item.PoiId, item => (item.PoiName, item.Latitude, item.Longitude));
        var unknownIds = proposedPoiIds.Where(id => !known.ContainsKey(id)).ToArray();
        var added = await dbContext.PointsOfInterest.AsNoTracking()
            .Where(poi => unknownIds.Contains(poi.Id))
            .ToDictionaryAsync(poi => poi.Id, cancellationToken);
        var mandatoryIds = System.Text.Json.JsonSerializer.Deserialize<long[]>(request.MandatoryPoiIdsJson) ?? [];

        var proposedStops = visits.Select(item =>
        {
            var poiId = item.PointOfInterestId!.Value;
            var (name, latitude, longitude) = known.TryGetValue(poiId, out var existing)
                ? existing
                : (added[poiId].Name, added[poiId].Latitude, added[poiId].Longitude);
            var change = !basePoiIds.Contains(poiId)
                ? ReroutingStopChanges.Added
                : keptInOrder.Contains(poiId) ? ReroutingStopChanges.Kept : ReroutingStopChanges.Moved;
            return new ReroutingProposedStop(
                poiId,
                name,
                latitude,
                longitude,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                mandatoryIds.Contains(poiId) || pending.Any(stop => stop.PoiId == poiId && stop.IsMandatory),
                item.TransportModeToNext?.ToString(),
                change);
        }).ToArray();

        return new ReroutingProposalSnapshot(
            ReroutingProposalSnapshot.CurrentSchemaVersion,
            winner.Variant.Strategy,
            ReasonCode(incident.IncidentType),
            session.ItineraryId,
            session.Itinerary.Version,
            new ReroutingAnchorSnapshot(anchor.StartAtUtc, anchor.Start.Latitude, anchor.Start.Longitude, anchor.AvailableMinutes),
            pending.Select(item => new ReroutingBaseStop(
                item.ItineraryItemId,
                item.PoiId,
                item.PoiName,
                item.Latitude,
                item.Longitude,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                item.IsMandatory)).ToArray(),
            proposedStops,
            basePoiIds.Where(id => !proposedPoiIds.Contains(id)).ToArray(),
            [incident.AffectedItineraryItemId!.Value],
            winner.Diffs!.Travel,
            winner.Diffs.Arrival,
            plan.EndAtUtc);
    }

    private static string ReasonCode(string incidentType) => incidentType switch
    {
        Incident.SevereWeatherType => TripAlertResponse.SevereWeatherMessageCode,
        Incident.ScheduleDelayType => TripAlertResponse.ScheduleDelayMessageCode,
        Incident.RouteDeviationType => TripAlertResponse.RouteDeviationMessageCode,
        Incident.PoiClosureType => TripAlertResponse.PoiClosureMessageCode,
        _ => throw new ArgumentOutOfRangeException(nameof(incidentType)),
    };

    /// <summary>The POIs that keep their relative order in both plans; the others that exist in both are "Moved".</summary>
    private static HashSet<long> LongestCommonSubsequence(IReadOnlyList<long> first, IReadOnlyList<long> second)
    {
        var lengths = new int[first.Count + 1, second.Count + 1];
        for (var i = first.Count - 1; i >= 0; i--)
        {
            for (var j = second.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = first[i] == second[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var common = new HashSet<long>();
        int row = 0, column = 0;
        while (row < first.Count && column < second.Count)
        {
            if (first[row] == second[column])
            {
                common.Add(first[row]);
                row++;
                column++;
            }
            else if (lengths[row + 1, column] >= lengths[row, column + 1])
            {
                row++;
            }
            else
            {
                column++;
            }
        }

        return common;
    }

    private sealed record StoredItem(
        long Id,
        long ItineraryId,
        long? PoiId,
        decimal? EstimatedCost,
        int? TravelMinutes);

    private sealed record PlanDifferences(int Travel, int Arrival);

    private sealed record Candidate(
        ReroutingVariant Variant,
        GeneratedItineraryPlan? Plan,
        int Index,
        PlanDifferences? Diffs = null);
}
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed class GeneratedItineraryInvariantValidator(SchedulingGenerationOptions? options = null)
{
    private readonly SchedulingGenerationOptions _options = options ?? new SchedulingGenerationOptions();

    public void Validate(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> candidates,
        GeneratedItineraryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Items.Count == 0)
        {
            throw new InvalidOperationException("Invariant violation: plan contains no items.");
        }

        var candidateMap = candidates.ToDictionary(c => c.Id);
        var candidateMatrixIndices = new Dictionary<long, int>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            candidateMatrixIndices[candidates[i].Id] = i + 1;
        }

        var itemsList = plan.Items.ToList();

        // 1. Sequence numbers must be contiguous starting from 1
        for (var i = 0; i < itemsList.Count; i++)
        {
            if (itemsList[i].SequenceNo != i + 1)
            {
                throw new InvalidOperationException("Invariant violation: sequence numbers are not contiguous starting at 1.");
            }
        }

        // 2. Must contain at least one visit
        var visitItems = itemsList.Where(it => it.Kind == ItineraryItemKind.Visit).ToList();
        if (visitItems.Count == 0)
        {
            throw new InvalidOperationException("Invariant violation: plan contains no visit items.");
        }

        // 3. Visit IDs must exist, be unique, and include all mandatory POIs exactly once
        var visitedIds = new HashSet<long>();
        foreach (var visit in visitItems)
        {
            if (visit.PointOfInterestId is not { } poiId)
            {
                throw new InvalidOperationException("Invariant violation: visit item has no POI ID.");
            }

            if (!candidateMap.TryGetValue(poiId, out var candidate))
            {
                throw new InvalidOperationException("Invariant violation: visit POI ID not found in candidate catalog.");
            }

            if (!visitedIds.Add(poiId))
            {
                throw new InvalidOperationException("Invariant violation: duplicate visit POI ID found in plan.");
            }

            if (visit.PointOfInterestName != candidate.Name)
            {
                throw new InvalidOperationException("Invariant violation: visit item POI name mismatch.");
            }

            var isMandatory = input.MandatoryPoiIds.Contains(poiId);
            if (visit.IsMandatory != isMandatory)
            {
                throw new InvalidOperationException("Invariant violation: visit item IsMandatory flag does not match request.");
            }

            var expectedReason = isMandatory ? "Mandatory location" : "Suggested nearby location";
            if (visit.RecommendationReason != expectedReason)
            {
                throw new InvalidOperationException("Invariant violation: visit item recommendation reason does not match request.");
            }
        }

        foreach (var mandatoryId in input.MandatoryPoiIds)
        {
            if (!visitedIds.Contains(mandatoryId))
            {
                throw new InvalidOperationException("Invariant violation: mandatory POI ID is missing from plan visits.");
            }
        }

        // 4. Rest item invariants
        foreach (var item in itemsList.Where(it => it.Kind == ItineraryItemKind.Rest))
        {
            if (item.IsMandatory)
            {
                throw new InvalidOperationException("Invariant violation: rest item cannot be marked mandatory.");
            }

            if (item.PointOfInterestId is { } restPoiId)
            {
                if (input.MandatoryPoiIds.Contains(restPoiId))
                {
                    throw new InvalidOperationException("Invariant violation: rest item cannot use a mandatory POI.");
                }

                if (!candidateMap.TryGetValue(restPoiId, out var restCandidate)
                    || !ItineraryScheduleEvaluator.IsQualifiedRestCandidate(restCandidate))
                {
                    throw new InvalidOperationException("Invariant violation: rest item POI is not a qualified rest candidate.");
                }

                if (item.RecommendationReason != "Suggested rest stop")
                {
                    throw new InvalidOperationException("Invariant violation: rest POI item recommendation reason mismatch.");
                }
            }
            else
            {
                if (item.RecommendationReason != "Free/rest time")
                {
                    throw new InvalidOperationException("Invariant violation: free rest item recommendation reason mismatch.");
                }
            }
        }

        // 5. Timeline, directional travel, transition/final buffers, and opening hours
        var currentTime = input.StartAtUtc;
        var previousIndex = 0;
        var totalCost = 0m;
        var continuousMinutes = 0;
        var restCount = 0;
        var restRequired = false;
        var locationIndices = new int[itemsList.Count];

        for (var i = 0; i < itemsList.Count; i++)
        {
            var item = itemsList[i];

            if (item.Kind == ItineraryItemKind.Visit)
            {
                if (restRequired)
                {
                    throw new InvalidOperationException("Invariant violation: required rest is missing or misplaced.");
                }

                var candidate = candidateMap[item.PointOfInterestId!.Value];
                var candidateIndex = candidateMatrixIndices[candidate.Id];
                locationIndices[i] = candidateIndex;

                var legTravel = matrix.GetMinutes(previousIndex, candidateIndex);
                var minArrival = currentTime.AddMinutes(legTravel + _options.TransitionBufferMinutes);
                continuousMinutes += legTravel + _options.TransitionBufferMinutes;
                var expectedArrival = ItineraryScheduleEvaluator.AlignToOpeningHours(
                    minArrival,
                    candidate,
                    input.TimeZone);

                if (item.PlannedArrivalUtc != expectedArrival)
                {
                    throw new InvalidOperationException("Invariant violation: planned arrival does not match deterministic travel, buffer, and opening hours alignment.");
                }

                var expectedDeparture = item.PlannedArrivalUtc.AddMinutes(candidate.VisitDurationMinutes);
                if (item.PlannedDepartureUtc != expectedDeparture)
                {
                    throw new InvalidOperationException("Invariant violation: planned departure does not match arrival + visit duration.");
                }

                if (!ItineraryScheduleEvaluator.FitsOpeningHours(item.PlannedArrivalUtc, item.PlannedDepartureUtc, candidate, input.TimeZone))
                {
                    throw new InvalidOperationException("Invariant violation: visit item violates candidate opening hours.");
                }

                if (item.EstimatedCost != candidate.EstimatedVisitCost)
                {
                    throw new InvalidOperationException("Invariant violation: visit item estimated cost does not match candidate cost.");
                }

                if (input.BudgetVnd.HasValue && candidate.EstimatedVisitCost is null)
                {
                    throw new InvalidOperationException("Invariant violation: visit with unknown cost is not allowed when a budget is present.");
                }

                totalCost += candidate.EstimatedVisitCost ?? 0m;
                continuousMinutes += candidate.VisitDurationMinutes;
                currentTime = item.PlannedDepartureUtc;
                previousIndex = candidateIndex;
                restRequired = ItineraryScheduleEvaluator.NeedsRest(input, continuousMinutes, restCount);
            }
            else if (item.Kind == ItineraryItemKind.Rest)
            {
                if (!restRequired)
                {
                    throw new InvalidOperationException("Invariant violation: rest item is extra or misplaced.");
                }

                var nextVisit = itemsList
                    .Skip(i + 1)
                    .FirstOrDefault(candidateItem => candidateItem.Kind == ItineraryItemKind.Visit);
                var nextIndex = nextVisit?.PointOfInterestId is { } nextPoiId
                    ? candidateMatrixIndices[nextPoiId]
                    : matrix.PointCount - 1;
                var expectedRestCandidate = FindExpectedRestCandidate(
                    candidates,
                    candidateMatrixIndices,
                    input,
                    previousIndex,
                    nextIndex,
                    currentTime,
                    matrix);

                if (item.PointOfInterestId is { } restPoiId)
                {
                    if (expectedRestCandidate?.Id != restPoiId)
                    {
                        throw new InvalidOperationException("Invariant violation: rest POI does not match qualified-rest selection semantics.");
                    }

                    var restCandidate = candidateMap[restPoiId];
                    var restIndex = candidateMatrixIndices[restPoiId];
                    locationIndices[i] = restIndex;

                    if (item.PointOfInterestName != restCandidate.Name || item.EstimatedCost is not null)
                    {
                        throw new InvalidOperationException("Invariant violation: rest POI metadata mismatch.");
                    }

                    var restLegTravel = matrix.GetMinutes(previousIndex, restIndex);
                    var minRestArrival = currentTime.AddMinutes(restLegTravel + _options.TransitionBufferMinutes);

                    if (item.PlannedArrivalUtc != minRestArrival)
                    {
                        throw new InvalidOperationException("Invariant violation: rest arrival does not match deterministic travel and buffer.");
                    }

                    var expectedRestDeparture = item.PlannedArrivalUtc.AddMinutes(30);
                    if (item.PlannedDepartureUtc != expectedRestDeparture)
                    {
                        throw new InvalidOperationException("Invariant violation: rest departure does not match arrival + 30 minutes.");
                    }

                    if (!ItineraryScheduleEvaluator.FitsOpeningHours(item.PlannedArrivalUtc, item.PlannedDepartureUtc, restCandidate, input.TimeZone))
                    {
                        throw new InvalidOperationException("Invariant violation: rest stop violates opening hours.");
                    }

                    currentTime = item.PlannedDepartureUtc;
                    previousIndex = restIndex;
                }
                else
                {
                    if (expectedRestCandidate is not null || item.EstimatedCost is not null)
                    {
                        throw new InvalidOperationException("Invariant violation: free rest does not match qualified-rest selection semantics.");
                    }

                    locationIndices[i] = previousIndex;

                    if (item.PlannedArrivalUtc != currentTime)
                    {
                        throw new InvalidOperationException("Invariant violation: free rest arrival does not match current time.");
                    }

                    var expectedRestDeparture = currentTime.AddMinutes(30);
                    if (item.PlannedDepartureUtc != expectedRestDeparture)
                    {
                        throw new InvalidOperationException("Invariant violation: free rest departure does not match arrival + 30 minutes.");
                    }

                    currentTime = expectedRestDeparture;
                }

                restCount++;
                continuousMinutes = 0;
                restRequired = false;
            }
        }

        if (restRequired)
        {
            throw new InvalidOperationException("Invariant violation: required rest is missing at the end of the visit sequence.");
        }

        // Final leg back to requested End point
        var finalLeg = matrix.GetMinutes(previousIndex, matrix.PointCount - 1);
        var expectedEndTime = currentTime.AddMinutes(finalLeg + _options.FinalReturnBufferMinutes);

        if (plan.EndAtUtc != expectedEndTime)
        {
            throw new InvalidOperationException("Invariant violation: plan end time does not match timeline + final return leg and buffer.");
        }

        var expectedDuration = (int)Math.Ceiling((plan.EndAtUtc - input.StartAtUtc).TotalMinutes);
        if (plan.TotalDurationMinutes != expectedDuration)
        {
            throw new InvalidOperationException("Invariant violation: plan total duration minutes does not match ceiling of end minus start.");
        }

        if (plan.EndAtUtc > input.StartAtUtc.AddMinutes(input.AvailableMinutes))
        {
            throw new InvalidOperationException("Invariant violation: plan end time exceeds available minutes horizon.");
        }

        // 6. Travel durations between consecutive items
        for (var i = 0; i < itemsList.Count; i++)
        {
            if (i == itemsList.Count - 1)
            {
                if (itemsList[i].TravelDurationToNextMinutes is not null)
                {
                    throw new InvalidOperationException("Invariant violation: last item must have null TravelDurationToNextMinutes.");
                }
            }
            else
            {
                var expectedTravel = matrix.GetMinutes(locationIndices[i], locationIndices[i + 1]);
                if (itemsList[i].TravelDurationToNextMinutes != expectedTravel)
                {
                    throw new InvalidOperationException("Invariant violation: TravelDurationToNextMinutes does not match matrix travel duration.");
                }
            }
        }

        // 7. Costs and budget
        if (plan.TotalEstimatedCost != totalCost)
        {
            throw new InvalidOperationException("Invariant violation: plan TotalEstimatedCost does not match sum of item costs.");
        }

        if (input.BudgetVnd.HasValue && plan.TotalEstimatedCost > input.BudgetVnd.Value)
        {
            throw new InvalidOperationException("Invariant violation: plan TotalEstimatedCost exceeds budget.");
        }
    }

    private GenerationCandidate? FindExpectedRestCandidate(
        IReadOnlyList<GenerationCandidate> candidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        GenerationInput input,
        int previousIndex,
        int nextIndex,
        DateTimeOffset currentTime,
        RouteDurationMatrix matrix)
    {
        return candidates
            .Where(candidate =>
                !input.MandatoryPoiIds.Contains(candidate.Id)
                && ItineraryScheduleEvaluator.IsQualifiedRestCandidate(candidate))
            .OrderBy(candidate => matrix.GetMinutes(
                previousIndex,
                candidateMatrixIndices[candidate.Id]))
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault(candidate =>
            {
                var restIndex = candidateMatrixIndices[candidate.Id];
                var restArrival = currentTime.AddMinutes(
                    matrix.GetMinutes(previousIndex, restIndex)
                    + _options.TransitionBufferMinutes);
                var restDeparture = restArrival.AddMinutes(30);
                var nextArrival = restDeparture.AddMinutes(
                    matrix.GetMinutes(restIndex, nextIndex)
                    + (nextIndex == matrix.PointCount - 1
                        ? _options.FinalReturnBufferMinutes
                        : _options.TransitionBufferMinutes));
                return ItineraryScheduleEvaluator.FitsOpeningHours(
                        restArrival,
                        restDeparture,
                        candidate,
                        input.TimeZone)
                    && nextArrival <= input.StartAtUtc.AddMinutes(input.AvailableMinutes);
            });
    }
}
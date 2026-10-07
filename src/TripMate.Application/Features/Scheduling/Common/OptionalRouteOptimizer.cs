using System.Diagnostics;

using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed class OptionalRouteOptimizer(
    ItineraryScheduleEvaluator evaluator,
    SchedulingGenerationOptions? options = null)
{
    private readonly ItineraryScheduleEvaluator _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly SchedulingGenerationOptions _options = options ?? new SchedulingGenerationOptions();

    public OptimizationResult? Optimize(
        GenerationInput input,
        IReadOnlyList<GenerationCandidate> mandatoryCandidates,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sortedOptionals = SortOptionalsCanonical(
            matrixCandidates,
            input.MandatoryPoiIds,
            candidateMatrixIndices,
            matrix);

        var comparator = new ScheduleGlobalComparator(sortedOptionals);

        // 1. Generate legacy baselines for all mandatory permutations
        var feasibleBaselines = new List<EvaluatedItinerarySchedule>();
        var mandatoryPermutations = Permute(mandatoryCandidates).ToArray();

        foreach (var mandatorySeq in mandatoryPermutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var legacyBaseline = BuildLegacyBaseline(
                input,
                mandatorySeq,
                matrix,
                matrixCandidates,
                candidateMatrixIndices,
                sortedOptionals);

            if (legacyBaseline is not null)
            {
                feasibleBaselines.Add(legacyBaseline);
            }
        }

        if (feasibleBaselines.Count == 0)
        {
            return null;
        }

        feasibleBaselines.Sort(comparator);
        var bestLegacy = feasibleBaselines[0];

        if (!_options.EnableOptionalRouteOptimization)
        {
            return new OptimizationResult(
                bestLegacy,
                bestLegacy,
                EvaluationsCount: 0,
                SeedCount: 0,
                ReconsideredAdmissionsCount: 0,
                TwoOptMovesCount: 0,
                RelocateMovesCount: 0,
                BudgetExhausted: false,
                ElapsedOptimization: TimeSpan.Zero);
        }

        var sw = Stopwatch.StartNew();
        var evaluationsBudget = _options.MaxRouteEvaluations;
        var evaluationsCount = 0;
        var twoOptMoves = 0;
        var relocateMoves = 0;
        var reconsideredAdmissions = 0;

        // 2. Select up to MaxRouteOptimizationSeeds from feasible baselines
        var seedCount = Math.Min(_options.MaxRouteOptimizationSeeds, feasibleBaselines.Count);
        var seeds = feasibleBaselines.Take(seedCount).ToArray();

        var candidateMap = matrixCandidates.ToDictionary(c => c.Id);
        var candidatesByRank = sortedOptionals
            .Where(c => !input.BudgetVnd.HasValue || c.EstimatedVisitCost.HasValue)
            .ToArray();

        var candidatePool = new List<EvaluatedItinerarySchedule>(feasibleBaselines.Count + seedCount)
        {
            bestLegacy,
        };

        // 3. For each seed, run cheapest feasible insertion, local improvement, and reconsideration
        foreach (var seedSchedule in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evaluationsBudget <= 0) break;

            // Extract the mandatory sequence order from this seed
            var currentMandatoryOrder = seedSchedule.VisitPoiIds
                .Where(id => input.MandatoryPoiIds.Contains(id))
                .Select(id => candidateMap[id])
                .ToList();

            var currentSequence = new List<GenerationCandidate>(currentMandatoryOrder);
            var currentSchedule = currentSequence.Count > 0
                ? _evaluator.Evaluate(input, currentSequence, matrix, matrixCandidates, candidateMatrixIndices, cancellationToken)
                : null;

            if (currentSchedule is null && currentSequence.Count > 0)
            {
                continue;
            }

            var skippedCandidates = new List<GenerationCandidate>();

            // Run initial insertion pass for optionals in canonical order
            foreach (var optCandidate in candidatesByRank)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (evaluationsBudget <= 0)
                {
                    skippedCandidates.Add(optCandidate);
                    continue;
                }

                var bestInsertion = FindBestFeasibleInsertion(
                    input,
                    currentSequence,
                    optCandidate,
                    matrix,
                    matrixCandidates,
                    candidateMatrixIndices,
                    ref evaluationsBudget,
                    ref evaluationsCount,
                    cancellationToken);

                if (bestInsertion is not null)
                {
                    currentSequence = bestInsertion.Value.Sequence;
                    currentSchedule = bestInsertion.Value.Schedule;
                }
                else
                {
                    skippedCandidates.Add(optCandidate);
                }
            }

            // Loop: Bounded local improvement and skipped-candidate reconsideration
            while (evaluationsBudget > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (currentSequence.Count >= 2 && currentSchedule is not null)
                {
                    RunLocalImprovement(
                        input,
                        ref currentSequence,
                        ref currentSchedule,
                        matrix,
                        matrixCandidates,
                        candidateMatrixIndices,
                        ref evaluationsBudget,
                        ref evaluationsCount,
                        ref twoOptMoves,
                        ref relocateMoves,
                        cancellationToken);
                }

                if (skippedCandidates.Count == 0 || evaluationsBudget <= 0)
                {
                    break;
                }

                var admittedAnyInRound = false;
                var nextSkipped = new List<GenerationCandidate>();

                foreach (var skipped in skippedCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (evaluationsBudget <= 0)
                    {
                        nextSkipped.Add(skipped);
                        continue;
                    }

                    var bestInsertion = FindBestFeasibleInsertion(
                        input,
                        currentSequence,
                        skipped,
                        matrix,
                        matrixCandidates,
                        candidateMatrixIndices,
                        ref evaluationsBudget,
                        ref evaluationsCount,
                        cancellationToken);

                    if (bestInsertion is not null)
                    {
                        currentSequence = bestInsertion.Value.Sequence;
                        currentSchedule = bestInsertion.Value.Schedule;
                        admittedAnyInRound = true;
                        reconsideredAdmissions++;
                    }
                    else
                    {
                        nextSkipped.Add(skipped);
                    }
                }

                skippedCandidates = nextSkipped;

                if (!admittedAnyInRound)
                {
                    break;
                }
            }

            if (currentSchedule is not null)
            {
                candidatePool.Add(currentSchedule);
            }
        }

        // 4. Select the best plan under global quality comparator
        candidatePool.Sort(comparator);
        var bestPlan = candidatePool[0];
        sw.Stop();

        return new OptimizationResult(
            bestPlan,
            bestLegacy,
            evaluationsCount,
            seedCount,
            reconsideredAdmissions,
            twoOptMoves,
            relocateMoves,
            evaluationsBudget <= 0,
            sw.Elapsed);
    }

    private void RunLocalImprovement(
        GenerationInput input,
        ref List<GenerationCandidate> currentSequence,
        ref EvaluatedItinerarySchedule currentSchedule,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        ref int evaluationsBudget,
        ref int evaluationsCount,
        ref int twoOptMoves,
        ref int relocateMoves,
        CancellationToken cancellationToken)
    {
        var improved = true;
        while (improved && evaluationsBudget > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            improved = false;

            (List<GenerationCandidate> Sequence, EvaluatedItinerarySchedule Schedule, bool IsTwoOpt)? bestProposal = null;

            // 1. Enumerate 2-opt proposals in stable index order
            for (var i = 0; i < currentSequence.Count - 1 && evaluationsBudget > 0; i++)
            {
                for (var j = i + 1; j < currentSequence.Count && evaluationsBudget > 0; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    evaluationsBudget--;
                    evaluationsCount++;

                    var proposed = ReverseSegment(currentSequence, i, j);
                    var schedule = _evaluator.Evaluate(
                        input,
                        proposed,
                        matrix,
                        matrixCandidates,
                        candidateMatrixIndices,
                        cancellationToken);

                    if (schedule is null) continue;

                    if (CompareLocalObjective(schedule, currentSchedule) < 0)
                    {
                        if (bestProposal is null || CompareLocalObjective(schedule, bestProposal.Value.Schedule) < 0)
                        {
                            bestProposal = (proposed, schedule, true);
                        }
                    }
                }
            }

            // 2. Enumerate relocate proposals in stable index order
            for (var i = 0; i < currentSequence.Count && evaluationsBudget > 0; i++)
            {
                for (var j = 0; j < currentSequence.Count && evaluationsBudget > 0; j++)
                {
                    if (i == j) continue;

                    cancellationToken.ThrowIfCancellationRequested();
                    evaluationsBudget--;
                    evaluationsCount++;

                    var proposed = Relocate(currentSequence, i, j);
                    var schedule = _evaluator.Evaluate(
                        input,
                        proposed,
                        matrix,
                        matrixCandidates,
                        candidateMatrixIndices,
                        cancellationToken);

                    if (schedule is null) continue;

                    if (CompareLocalObjective(schedule, currentSchedule) < 0)
                    {
                        if (bestProposal is null || CompareLocalObjective(schedule, bestProposal.Value.Schedule) < 0)
                        {
                            bestProposal = (proposed, schedule, false);
                        }
                    }
                }
            }

            if (bestProposal is not null)
            {
                currentSequence = bestProposal.Value.Sequence;
                currentSchedule = bestProposal.Value.Schedule;
                if (bestProposal.Value.IsTwoOpt) twoOptMoves++;
                else relocateMoves++;
                improved = true;
            }
        }
    }

    private static List<GenerationCandidate> ReverseSegment(List<GenerationCandidate> sequence, int start, int end)
    {
        var result = new List<GenerationCandidate>(sequence.Count);
        for (var i = 0; i < start; i++) result.Add(sequence[i]);
        for (var i = end; i >= start; i--) result.Add(sequence[i]);
        for (var i = end + 1; i < sequence.Count; i++) result.Add(sequence[i]);
        return result;
    }

    private static List<GenerationCandidate> Relocate(List<GenerationCandidate> sequence, int from, int to)
    {
        var result = new List<GenerationCandidate>(sequence.Count);
        var item = sequence[from];
        for (var i = 0; i < sequence.Count; i++)
        {
            if (i == from) continue;
            if (i == to && from > to) result.Add(item);
            result.Add(sequence[i]);
            if (i == to && from < to) result.Add(item);
        }
        return result;
    }

    private static int CompareLocalObjective(
        EvaluatedItinerarySchedule a,
        EvaluatedItinerarySchedule b)
    {
        // 1. total directional matrix travel minutes including start and end, ascending
        var travelCmp = a.TotalMatrixTravelMinutes.CompareTo(b.TotalMatrixTravelMinutes);
        if (travelCmp != 0) return travelCmp;

        // 2. final end time/total duration ascending
        var durationCmp = a.TotalDurationMinutes.CompareTo(b.TotalDurationMinutes);
        if (durationCmp != 0) return durationCmp;

        var endCmp = a.EndAtUtc.CompareTo(b.EndAtUtc);
        if (endCmp != 0) return endCmp;

        var minLen = Math.Min(a.VisitPoiIds.Count, b.VisitPoiIds.Count);
        // 3. visit-ID sequence lexicographically ascending
        for (var i = 0; i < minLen; i++)
        {
            var idCmp = a.VisitPoiIds[i].CompareTo(b.VisitPoiIds[i]);
            if (idCmp != 0) return idCmp;
        }

        return a.VisitPoiIds.Count.CompareTo(b.VisitPoiIds.Count);
    }

    private (List<GenerationCandidate> Sequence, EvaluatedItinerarySchedule Schedule)? FindBestFeasibleInsertion(
        GenerationInput input,
        List<GenerationCandidate> currentSequence,
        GenerationCandidate candidateToInsert,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        ref int evaluationsBudget,
        ref int evaluationsCount,
        CancellationToken cancellationToken)
    {
        var slots = new List<(int Slot, int AddedTravel)>(currentSequence.Count + 1);
        var candIndex = candidateMatrixIndices[candidateToInsert.Id];

        for (var k = 0; k <= currentSequence.Count; k++)
        {
            var prevIndex = k == 0 ? 0 : candidateMatrixIndices[currentSequence[k - 1].Id];
            var nextIndex = k == currentSequence.Count ? matrix.PointCount - 1 : candidateMatrixIndices[currentSequence[k].Id];
            var addedTravel = matrix.GetMinutes(prevIndex, candIndex)
                + matrix.GetMinutes(candIndex, nextIndex)
                - matrix.GetMinutes(prevIndex, nextIndex);
            slots.Add((k, addedTravel));
        }

        // The declared insertion tie-break prefers the lowest slot index.
        slots.Sort((a, b) =>
        {
            var cmp = a.AddedTravel.CompareTo(b.AddedTravel);
            return cmp != 0 ? cmp : a.Slot.CompareTo(b.Slot);
        });

        (List<GenerationCandidate> Sequence, EvaluatedItinerarySchedule Schedule, int AddedTravel, int Slot)? bestProposal = null;

        foreach (var (slot, addedTravel) in slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evaluationsBudget <= 0) break;

            evaluationsBudget--;
            evaluationsCount++;

            var proposedSequence = new List<GenerationCandidate>(currentSequence.Count + 1);
            for (var i = 0; i < slot; i++) proposedSequence.Add(currentSequence[i]);
            proposedSequence.Add(candidateToInsert);
            for (var i = slot; i < currentSequence.Count; i++) proposedSequence.Add(currentSequence[i]);

            var schedule = _evaluator.Evaluate(
                input,
                proposedSequence,
                matrix,
                matrixCandidates,
                candidateMatrixIndices,
                cancellationToken);

            if (schedule is null) continue;

            if (bestProposal is null)
            {
                bestProposal = (proposedSequence, schedule, addedTravel, slot);
            }
            else
            {
                // Compare by:
                // 1. lowest added travel
                // 2. lowest resulting total travel
                // 3. earliest final end time
                // 4. lowest insertion position
                // 5. lexicographically lowest visit-ID sequence
                var cmp = addedTravel.CompareTo(bestProposal.Value.AddedTravel);
                if (cmp < 0)
                {
                    bestProposal = (proposedSequence, schedule, addedTravel, slot);
                }
                else if (cmp == 0)
                {
                    var totalTravelCmp = schedule.TotalMatrixTravelMinutes.CompareTo(bestProposal.Value.Schedule.TotalMatrixTravelMinutes);
                    if (totalTravelCmp < 0)
                    {
                        bestProposal = (proposedSequence, schedule, addedTravel, slot);
                    }
                    else if (totalTravelCmp == 0)
                    {
                        var endCmp = schedule.EndAtUtc.CompareTo(bestProposal.Value.Schedule.EndAtUtc);
                        if (endCmp < 0)
                        {
                            bestProposal = (proposedSequence, schedule, addedTravel, slot);
                        }
                        else if (endCmp == 0 && slot < bestProposal.Value.Slot)
                        {
                            bestProposal = (proposedSequence, schedule, addedTravel, slot);
                        }
                        else if (endCmp == 0
                            && slot == bestProposal.Value.Slot
                            && CompareVisitIdSequences(
                                schedule.VisitPoiIds,
                                bestProposal.Value.Schedule.VisitPoiIds) < 0)
                        {
                            bestProposal = (proposedSequence, schedule, addedTravel, slot);
                        }
                    }
                }
            }
        }

        return bestProposal is null ? null : (bestProposal.Value.Sequence, bestProposal.Value.Schedule);
    }

    private static int CompareVisitIdSequences(
        IReadOnlyList<long> first,
        IReadOnlyList<long> second)
    {
        var minimumLength = Math.Min(first.Count, second.Count);
        for (var i = 0; i < minimumLength; i++)
        {
            var comparison = first[i].CompareTo(second[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return first.Count.CompareTo(second.Count);
    }

    public static IReadOnlyList<GenerationCandidate> SortOptionalsCanonical(
        IReadOnlyList<GenerationCandidate> candidates,
        IReadOnlyCollection<long> mandatoryPoiIds,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        RouteDurationMatrix matrix)
    {
        return candidates
            .Where(c => !mandatoryPoiIds.Contains(c.Id) && !ItineraryScheduleEvaluator.IsQualifiedRestCandidate(c))
            .OrderByDescending(c => c.EffectiveDesirabilityScore)
            .ThenByDescending(c => c.ScenicScoreForRanking ?? decimal.MinValue)
            .ThenByDescending(c => c.PhotoRatingForRanking ?? decimal.MinValue)
            .ThenBy(c => matrix.GetMinutes(0, candidateMatrixIndices[c.Id]))
            .ThenBy(c => c.EstimatedVisitCostForRanking ?? decimal.MaxValue)
            .ThenBy(c => c.Id)
            .ToArray();
    }

    public EvaluatedItinerarySchedule? BuildLegacyBaseline(
        GenerationInput input,
        IReadOnlyList<GenerationCandidate> sequence,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        IReadOnlyList<GenerationCandidate> optionalCandidates)
    {
        var currentTime = input.StartAtUtc;
        var continuousMinutes = 0;
        var totalCost = 0m;
        var items = new List<GeneratedItineraryItem>();
        var previousIndex = 0;
        var totalMatrixTravel = 0;

        for (var sequenceIndex = 0; sequenceIndex < sequence.Count; sequenceIndex++)
        {
            var candidate = sequence[sequenceIndex];
            var candidateIndex = candidateMatrixIndices[candidate.Id];
            var legTravel = matrix.GetMinutes(previousIndex, candidateIndex);
            totalMatrixTravel += legTravel;
            var travelMinutes = legTravel + _options.TransitionBufferMinutes;
            currentTime = currentTime.AddMinutes(travelMinutes);
            continuousMinutes += travelMinutes;

            currentTime = ItineraryScheduleEvaluator.AlignToOpeningHours(currentTime, candidate, input.TimeZone);
            if (currentTime == DateTimeOffset.MinValue) return null;

            var departureTime = currentTime.AddMinutes(candidate.VisitDurationMinutes);
            if (!ItineraryScheduleEvaluator.FitsOpeningHours(currentTime, departureTime, candidate, input.TimeZone)) return null;

            totalCost += candidate.EstimatedVisitCost ?? 0m;
            if (input.BudgetVnd.HasValue && totalCost > input.BudgetVnd.Value) return null;

            var isMandatoryStop = input.MandatoryPoiIds.Contains(candidate.Id);
            items.Add(new GeneratedItineraryItem(
                items.Count + 1,
                candidate.Id,
                candidate.Name,
                ItineraryItemKind.Visit,
                currentTime,
                departureTime,
                isMandatoryStop,
                candidate.EstimatedVisitCost,
                isMandatoryStop ? "Mandatory location" : "Suggested nearby location"));
            continuousMinutes += candidate.VisitDurationMinutes;
            currentTime = departureTime;
            previousIndex = candidateIndex;

            if (ItineraryScheduleEvaluator.NeedsRest(input, continuousMinutes, items.Count(item => item.Kind == ItineraryItemKind.Rest)))
            {
                var nextIndex = sequenceIndex + 1 < sequence.Count
                    ? candidateMatrixIndices[sequence[sequenceIndex + 1].Id]
                    : matrix.PointCount - 1;
                var restCandidate = _evaluator.FindQualifiedRestCandidate(
                    matrixCandidates,
                    candidateMatrixIndices,
                    input,
                    previousIndex,
                    nextIndex,
                    currentTime,
                    matrix);
                if (restCandidate is not null)
                {
                    var restIndex = candidateMatrixIndices[restCandidate.Id];
                    var restLeg = matrix.GetMinutes(previousIndex, restIndex);
                    totalMatrixTravel += restLeg;
                    var restArrival = currentTime.AddMinutes(restLeg + _options.TransitionBufferMinutes);
                    var restDeparture = restArrival.AddMinutes(30);
                    items.Add(new GeneratedItineraryItem(
                        items.Count + 1,
                        restCandidate.Id,
                        restCandidate.Name,
                        ItineraryItemKind.Rest,
                        restArrival,
                        restDeparture,
                        false,
                        null,
                        "Suggested rest stop"));
                    currentTime = restDeparture;
                    previousIndex = restIndex;
                }
                else
                {
                    var restEnd = currentTime.AddMinutes(30);
                    items.Add(new GeneratedItineraryItem(
                        items.Count + 1,
                        null,
                        null,
                        ItineraryItemKind.Rest,
                        currentTime,
                        restEnd,
                        false,
                        null,
                        "Free/rest time"));
                    currentTime = restEnd;
                }
                continuousMinutes = 0;
            }
        }

        foreach (var candidate in optionalCandidates)
        {
            if (input.BudgetVnd.HasValue && candidate.EstimatedVisitCost is null) continue;

            var candidateIndex = candidateMatrixIndices[candidate.Id];
            var legTravel = matrix.GetMinutes(previousIndex, candidateIndex);
            var travelMinutes = legTravel + _options.TransitionBufferMinutes;
            var arrivalTime = currentTime.AddMinutes(travelMinutes);
            arrivalTime = ItineraryScheduleEvaluator.AlignToOpeningHours(arrivalTime, candidate, input.TimeZone);
            if (arrivalTime == DateTimeOffset.MinValue) continue;

            var departureTime = arrivalTime.AddMinutes(candidate.VisitDurationMinutes);
            var proposedCost = totalCost + (candidate.EstimatedVisitCost ?? 0m);
            var continuousMinutesAfterVisit = continuousMinutes + travelMinutes + candidate.VisitDurationMinutes;

            var nextCandidate = optionalCandidates
                .SkipWhile(optionalCandidate => optionalCandidate.Id != candidate.Id)
                .Skip(1)
                .FirstOrDefault();
            var nextIndex = nextCandidate is null ? matrix.PointCount - 1 : candidateMatrixIndices[nextCandidate.Id];

            var needsRest = ItineraryScheduleEvaluator.NeedsRest(input, continuousMinutesAfterVisit, items.Count(item => item.Kind == ItineraryItemKind.Rest));
            var restCandidate = needsRest
                ? _evaluator.FindQualifiedRestCandidate(matrixCandidates, candidateMatrixIndices, input, candidateIndex, nextIndex, departureTime, matrix)
                : null;

            GeneratedItineraryItem? restItem = null;
            DateTimeOffset? restEndUtc = null;
            var restPreviousIndex = candidateIndex;
            var restLegTravel = 0;

            if (needsRest)
            {
                if (restCandidate is not null)
                {
                    var optRestIndex = candidateMatrixIndices[restCandidate.Id];
                    restLegTravel = matrix.GetMinutes(candidateIndex, optRestIndex);
                    var restArrival = departureTime.AddMinutes(restLegTravel + _options.TransitionBufferMinutes);
                    restEndUtc = restArrival.AddMinutes(30);
                    restItem = new GeneratedItineraryItem(
                        0,
                        restCandidate.Id,
                        restCandidate.Name,
                        ItineraryItemKind.Rest,
                        restArrival,
                        restEndUtc.Value,
                        false,
                        null,
                        "Suggested rest stop");
                    restPreviousIndex = optRestIndex;
                }
                else
                {
                    restEndUtc = departureTime.AddMinutes(30);
                    restItem = new GeneratedItineraryItem(
                        0,
                        null,
                        null,
                        ItineraryItemKind.Rest,
                        departureTime,
                        restEndUtc.Value,
                        false,
                        null,
                        "Free/rest time");
                    restPreviousIndex = candidateIndex;
                }
            }

            var endTime = restEndUtc is null
                ? departureTime.AddMinutes(matrix.GetMinutes(candidateIndex, matrix.PointCount - 1) + _options.FinalReturnBufferMinutes)
                : restEndUtc.Value.AddMinutes(matrix.GetMinutes(restPreviousIndex, matrix.PointCount - 1) + _options.FinalReturnBufferMinutes);

            if (!ItineraryScheduleEvaluator.FitsOpeningHours(arrivalTime, departureTime, candidate, input.TimeZone)
                || (input.BudgetVnd.HasValue && proposedCost > input.BudgetVnd.Value)
                || endTime > input.StartAtUtc.AddMinutes(input.AvailableMinutes))
            {
                continue;
            }

            totalMatrixTravel += legTravel;
            items.Add(new GeneratedItineraryItem(
                items.Count + 1,
                candidate.Id,
                candidate.Name,
                ItineraryItemKind.Visit,
                arrivalTime,
                departureTime,
                false,
                candidate.EstimatedVisitCost,
                "Suggested nearby location"));
            totalCost = proposedCost;
            currentTime = restEndUtc ?? departureTime;
            previousIndex = restPreviousIndex;
            continuousMinutes = restItem is null ? continuousMinutesAfterVisit : 0;

            if (restItem is not null)
            {
                totalMatrixTravel += restLegTravel;
                items.Add(restItem with { SequenceNo = items.Count + 1 });
            }
        }

        if (items.All(item => item.Kind != ItineraryItemKind.Visit)) return null;

        var finalLeg = matrix.GetMinutes(previousIndex, matrix.PointCount - 1);
        totalMatrixTravel += finalLeg;
        currentTime = currentTime.AddMinutes(finalLeg + _options.FinalReturnBufferMinutes);

        if (currentTime > input.StartAtUtc.AddMinutes(input.AvailableMinutes)) return null;

        var itemsWithTravelDurations = AddTravelDurations(items, matrix, candidateMatrixIndices);
        var totalDuration = (int)Math.Ceiling((currentTime - input.StartAtUtc).TotalMinutes);

        var plan = new GeneratedItineraryPlan(
            itemsWithTravelDurations,
            currentTime,
            totalDuration,
            totalCost);

        var visitPoiIds = items
            .Where(it => it.Kind == ItineraryItemKind.Visit && it.PointOfInterestId.HasValue)
            .Select(it => it.PointOfInterestId!.Value)
            .ToArray();

        return new EvaluatedItinerarySchedule(
            plan,
            totalMatrixTravel,
            totalDuration,
            currentTime,
            totalCost,
            visitPoiIds);
    }

    private static IReadOnlyCollection<GeneratedItineraryItem> AddTravelDurations(
        IReadOnlyList<GeneratedItineraryItem> items,
        RouteDurationMatrix matrix,
        IReadOnlyDictionary<long, int> candidateMatrixIndices)
    {
        var locationIndices = new int[items.Count];
        var previousIndex = 0;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].PointOfInterestId is { } poiId)
            {
                previousIndex = candidateMatrixIndices[poiId];
            }

            locationIndices[index] = previousIndex;
        }

        return items
            .Select((item, index) => item with
            {
                TravelDurationToNextMinutes = index == items.Count - 1
                    ? null
                    : matrix.GetMinutes(locationIndices[index], locationIndices[index + 1]),
            })
            .ToArray();
    }

    private static IEnumerable<IReadOnlyList<GenerationCandidate>> Permute(
        IReadOnlyList<GenerationCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            yield return [];
            yield break;
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var remaining = candidates.Where((_, candidateIndex) => candidateIndex != index).ToArray();
            foreach (var suffix in Permute(remaining))
            {
                yield return new[] { candidate }.Concat(suffix).ToArray();
            }
        }
    }
}

public sealed class ScheduleGlobalComparator : IComparer<EvaluatedItinerarySchedule>
{
    private readonly IReadOnlyList<GenerationCandidate> _sortedOptionals;

    public ScheduleGlobalComparator(IReadOnlyList<GenerationCandidate> sortedOptionals)
    {
        _sortedOptionals = sortedOptionals;
    }

    public int Compare(EvaluatedItinerarySchedule? x, EvaluatedItinerarySchedule? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        // 1. optional-inclusion bit vector in canonical rank order, descending lexicographically
        for (var i = 0; i < _sortedOptionals.Count; i++)
        {
            var optId = _sortedOptionals[i].Id;
            var xHas = x.VisitPoiIds.Contains(optId);
            var yHas = y.VisitPoiIds.Contains(optId);
            if (xHas != yHas)
            {
                return xHas ? -1 : 1;
            }
        }

        // 2. total directional matrix travel minutes, ascending
        var travelComparison = x.TotalMatrixTravelMinutes.CompareTo(y.TotalMatrixTravelMinutes);
        if (travelComparison != 0) return travelComparison;

        // 3. total duration/final end time, ascending
        var durationComparison = x.TotalDurationMinutes.CompareTo(y.TotalDurationMinutes);
        if (durationComparison != 0) return durationComparison;

        var endComparison = x.EndAtUtc.CompareTo(y.EndAtUtc);
        if (endComparison != 0) return endComparison;

        var minLen = Math.Min(x.VisitPoiIds.Count, y.VisitPoiIds.Count);
        // 4. visit-ID sequence lexicographically ascending
        for (var i = 0; i < minLen; i++)
        {
            var idComparison = x.VisitPoiIds[i].CompareTo(y.VisitPoiIds[i]);
            if (idComparison != 0) return idComparison;
        }

        return x.VisitPoiIds.Count.CompareTo(y.VisitPoiIds.Count);
    }
}

public sealed record OptimizationResult(
    EvaluatedItinerarySchedule Schedule,
    EvaluatedItinerarySchedule BaselineSchedule,
    int EvaluationsCount,
    int SeedCount,
    int ReconsideredAdmissionsCount,
    int TwoOptMovesCount,
    int RelocateMovesCount,
    bool BudgetExhausted,
    TimeSpan ElapsedOptimization);
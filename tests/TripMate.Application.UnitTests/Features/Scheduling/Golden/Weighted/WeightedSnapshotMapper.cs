using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden.Weighted;

public static class WeightedSnapshotMapper
{
    public static WeightedSolverGoldenSnapshot From(
        GoldenScenario scenario,
        CspResult result,
        RoutingContext primaryCtx,
        WeightedObjectiveInputs inputs,
        IReadOnlyDictionary<long, int> nodeOf)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(primaryCtx);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(nodeOf);

        var stats = result.Statistics;
        Assert.False(stats.TimeLimitReached, $"Scenario {scenario.Id} timed out in Weighted mode! TimeLimitMilliseconds must be large enough.");

        var groupScratch = new int[inputs.DiversityGroupCount];
        var itemScratch = new List<GeneratedItineraryItem>(32);

        long? bestModelObjective = null;
        long? bestEvaluatedObjective = null;
        IReadOnlyList<long>? bestVisitPoiIds = null;
        int? bestTravelMinutes = null;
        int? bestDurationMinutes = null;
        decimal? bestEstimatedCost = null;
        DateTimeOffset? endAtUtc = null;

        if (result.Schedule is { } schedule)
        {
            bestVisitPoiIds = schedule.VisitPoiIds;
            bestTravelMinutes = schedule.TotalMatrixTravelMinutes;
            bestDurationMinutes = schedule.TotalDurationMinutes;
            bestEstimatedCost = schedule.TotalEstimatedCost;
            endAtUtc = schedule.EndAtUtc;

            // 1. Evaluated J trên lịch thực tế
            bestEvaluatedObjective = ObjectiveCalculator.Evaluate(
                inputs, primaryCtx, schedule, nodeOf, scenario.Indices, groupScratch, itemScratch);

            // 2. Model J từ sequence
            var visitItems = schedule.Plan.Items
                .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
                .OrderBy(i => i.SequenceNo)
                .ToList();

            if (visitItems.All(i => nodeOf.ContainsKey(i.PointOfInterestId!.Value)))
            {
                var seq = visitItems.Select(i => nodeOf[i.PointOfInterestId!.Value]).ToArray();
                if (RouteState.Build(primaryCtx, seq) is { } state)
                {
                    bestModelObjective = ObjectiveCalculator.Replay(
                        inputs, primaryCtx, state.Sequence, state.Earliest, groupScratch);
                }
            }
        }

        // Tính Model Objective cho từng Elite candidate
        var eliteObjectives = new List<long>(result.EliteVisitIds.Count);
        foreach (var eliteIds in result.EliteVisitIds)
        {
            if (eliteIds.All(nodeOf.ContainsKey))
            {
                var seq = eliteIds.Select(id => nodeOf[id]).ToArray();
                if (RouteState.Build(primaryCtx, seq) is { } state)
                {
                    var obj = ObjectiveCalculator.Replay(
                        inputs, primaryCtx, state.Sequence, state.Earliest, groupScratch);
                    eliteObjectives.Add(obj);
                }
            }
        }

        return new WeightedSolverGoldenSnapshot(
            scenario.Id,
            scenario.Source,
            result.Schedule != null,
            bestModelObjective,
            bestEvaluatedObjective,
            bestVisitPoiIds,
            eliteObjectives,
            result.EliteVisitIds,
            bestTravelMinutes,
            bestDurationMinutes,
            bestEstimatedCost,
            endAtUtc,
            stats.NodesExpanded,
            stats.PrunedByConstraints,
            stats.PrunedByForwardChecking,
            stats.PrunedByBound,
            stats.SolutionsFound,
            stats.NodesToFirstSolution,
            stats.SearchCompleted,
            stats.NodeLimitReached,
            stats.TimeLimitReached,
            stats.EvaluatorCalls,
            stats.UsedRelaxedRestModel,
            stats.ChoseRelaxedRestModel);
    }
}
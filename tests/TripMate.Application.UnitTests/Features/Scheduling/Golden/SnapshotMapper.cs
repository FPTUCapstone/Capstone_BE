using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Golden;

public static class SnapshotMapper
{
    public static SolverGoldenSnapshot From(GoldenScenario scenario, CspResult result, RoutingContext primaryCtx)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(primaryCtx);

        var stats = result.Statistics;
        Assert.False(stats.TimeLimitReached, $"Scenario {scenario.Id} timed out! TimeLimitMilliseconds must be large enough to guarantee determinism.");

        var driftMeasured = false;
        var maxDrift = 0;
        var travelDiff = 0;

        // Chỉ đo drift khi có kết quả hợp lệ, KHÔNG chọn mô hình nới lỏng (ChoseRelaxedRestModel == false),
        // và KHÔNG có điểm nghỉ trong lịch (Plan.Items không chứa Rest item).
        if (result.Schedule is { } schedule
            && !stats.ChoseRelaxedRestModel
            && !schedule.Plan.Items.Any(i => i.Kind == ItineraryItemKind.Rest))
        {
            var visitItems = schedule.Plan.Items
                .Where(i => i.Kind == ItineraryItemKind.Visit && i.PointOfInterestId.HasValue)
                .OrderBy(i => i.SequenceNo)
                .ToList();

            var nodeOf = new Dictionary<long, int>(primaryCtx.Nodes.Count);
            for (var node = 0; node < primaryCtx.Nodes.Count; node++)
            {
                nodeOf[primaryCtx.Nodes[node].Candidate.Id] = node;
            }

            if (visitItems.All(i => nodeOf.ContainsKey(i.PointOfInterestId!.Value)))
            {
                var seq = visitItems.Select(i => nodeOf[i.PointOfInterestId!.Value]).ToArray();
                if (RouteState.Build(primaryCtx, seq) is { } state)
                {
                    driftMeasured = true;
                    travelDiff = Math.Abs(state.TravelMinutes - schedule.TotalMatrixTravelMinutes);

                    for (var i = 0; i < visitItems.Count; i++)
                    {
                        var modelStart = state.Earliest[i];
                        var evalStart = ModelTime.ToModelMinute(scenario.Input.StartAtUtc, visitItems[i].PlannedArrivalUtc);
                        maxDrift = Math.Max(maxDrift, Math.Abs(modelStart - evalStart));
                    }
                }
            }
        }

        return new SolverGoldenSnapshot(
            scenario.Id,
            scenario.Source,
            scenario.ExpectedFeasible,
            result.Schedule is not null,
            result.Schedule?.VisitPoiIds,
            result.EliteVisitIds,
            result.Schedule?.TotalMatrixTravelMinutes,
            result.Schedule is null ? null : ModelTime.ToModelMinute(scenario.Input.StartAtUtc, result.Schedule.EndAtUtc),
            stats.NodesExpanded,
            stats.PrunedByConstraints,
            stats.PrunedByForwardChecking,
            stats.PrunedByBound,
            stats.SolutionsFound,
            stats.NodesToFirstSolution,
            stats.SearchCompleted,
            stats.NodeLimitReached,
            stats.TimeLimitReached,
            stats.InitialIncumbentAccepted,
            stats.ReturnedInitialIncumbent,
            stats.EvaluatorCalls,
            stats.InSearchEvaluationLimitReached,
            stats.TieEvaluationLimitReached,
            stats.UsedRelaxedRestModel,
            stats.ChoseRelaxedRestModel,
            driftMeasured,
            maxDrift,
            travelDiff);
    }
}
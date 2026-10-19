using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Xây dựng incumbent ban đầu tham lam (Greedy Initial Incumbent) cho chế độ WeightedUtility (PR-07c H2).
/// 1. Chèn các điểm bắt buộc vào vị trí làm J mô hình giảm ít nhất (vẫn khả thi).
/// 2. Vòng lặp: với mỗi điểm tùy chọn chưa có và mỗi vị trí khả thi, tính ΔJ qua RouteState.Build + ObjectiveCalculator.Replay.
///    Chọn cặp có ΔJ lớn nhất. Dừng khi ΔJ &lt;= 0 hoặc không còn cặp khả thi. Thứ tự duyệt tất định (node index, rồi vị trí).
/// </summary>
public static class GreedyWeightedIncumbent
{
    public static IReadOnlyList<int> BuildSequence(
        RoutingContext ctx,
        WeightedObjectiveInputs inputs,
        CspOptions options,
        IReadOnlyCollection<int>? initialDomain = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);

        var maxStops = options.MaxStops ?? int.MaxValue;
        maxStops = Math.Min(maxStops, ctx.Nodes.Count);

        var catScratch = new int[inputs.CategoryCount];
        var currentSeq = new List<int>(maxStops);

        // 1. Chèn các điểm bắt buộc vào vị trí khả thi có J cao nhất
        var mandatoryNodes = ctx.MandatoryNodes.OrderBy(m => m).ToList();
        foreach (var m in mandatoryNodes)
        {
            var bestPos = -1;
            var bestJ = long.MinValue;

            for (var pos = 0; pos <= currentSeq.Count; pos++)
            {
                currentSeq.Insert(pos, m);
                var route = RouteState.Build(ctx, currentSeq);
                if (route is not null && (ctx.Budget is null || route.Cost <= ctx.Budget.Value))
                {
                    var j = ObjectiveCalculator.Replay(inputs, ctx, route.Sequence, route.Earliest, catScratch);
                    if (j > bestJ)
                    {
                        bestJ = j;
                        bestPos = pos;
                    }
                }
                currentSeq.RemoveAt(pos);
            }

            if (bestPos < 0)
            {
                // Không chèn được điểm bắt buộc khả thi
                return [];
            }

            currentSeq.Insert(bestPos, m);
        }

        long currentJ = 0;
        if (currentSeq.Count > 0)
        {
            var route = RouteState.Build(ctx, currentSeq);
            if (route is null)
            {
                return [];
            }
            currentJ = ObjectiveCalculator.Replay(inputs, ctx, route.Sequence, route.Earliest, catScratch);
        }

        // 2. Vòng lặp chèn điểm tùy chọn có ΔJ lớn nhất
        var domainSet = initialDomain != null ? new HashSet<int>(initialDomain) : null;
        var optionalCandidates = new List<int>();
        for (var v = 0; v < ctx.Nodes.Count; v++)
        {
            if (ctx.Nodes[v].IsMandatory) continue;
            if (domainSet != null && !domainSet.Contains(v)) continue;
            optionalCandidates.Add(v);
        }
        optionalCandidates.Sort();

        while (currentSeq.Count < maxStops)
        {
            var bestNode = -1;
            var bestPos = -1;
            var bestDeltaJ = 0L;

            foreach (var v in optionalCandidates)
            {
                if (currentSeq.Contains(v)) continue;

                for (var pos = 0; pos <= currentSeq.Count; pos++)
                {
                    currentSeq.Insert(pos, v);
                    var route = RouteState.Build(ctx, currentSeq);
                    if (route is not null && (ctx.Budget is null || route.Cost <= ctx.Budget.Value))
                    {
                        var j = ObjectiveCalculator.Replay(inputs, ctx, route.Sequence, route.Earliest, catScratch);
                        var deltaJ = j - currentJ;
                        if (deltaJ > bestDeltaJ)
                        {
                            bestDeltaJ = deltaJ;
                            bestNode = v;
                            bestPos = pos;
                        }
                    }
                    currentSeq.RemoveAt(pos);
                }
            }

            if (bestNode < 0 || bestDeltaJ <= 0)
            {
                break;
            }

            currentSeq.Insert(bestPos, bestNode);
            currentJ += bestDeltaJ;
        }

        return currentSeq;
    }

    public static EvaluatedItinerarySchedule? Build(
        RoutingContext ctx,
        WeightedObjectiveInputs inputs,
        CspOptions options,
        Func<RoutingContext, IReadOnlyList<int>, CspStatistics, EvaluatedItinerarySchedule?> evaluate,
        CspStatistics? statistics = null,
        IReadOnlyCollection<int>? initialDomain = null)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        var seq = BuildSequence(ctx, inputs, options, initialDomain);
        if (seq.Count == 0)
        {
            return null;
        }

        return evaluate(ctx, seq, statistics ?? new CspStatistics());
    }
}

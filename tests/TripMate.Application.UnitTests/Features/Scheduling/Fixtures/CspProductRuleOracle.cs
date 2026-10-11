using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.UnitTests.Features.Scheduling.Fixtures;

/// <summary>
/// Oracle vét cạn theo product rule cho bài nhỏ. Duyệt mọi dãy có thứ tự của các nút được phép, không cắt nhánh ở tiền
/// tố không hợp lệ: với ma trận không metric, [a, b] có thể không kịp về thẳng đích nhưng [a, b, c] vẫn kịp. Giữ những
/// lộ trình hợp lệ trên mô hình (<see cref="RouteState.Build(RoutingContext, IReadOnlyList{int})"/>) có đủ điểm bắt buộc,
/// đưa từng lộ trình qua <see cref="ItineraryScheduleEvaluator"/>, và trả về lịch tốt nhất theo
/// <see cref="ScheduleGlobalComparator"/>, đúng quy tắc mà heuristic và CSP dùng để chọn lịch.
/// </summary>
public static class CspProductRuleOracle
{
    public static EvaluatedItinerarySchedule? Best(
        GenerationInput input,
        RouteDurationMatrix matrix,
        SchedulingGenerationOptions options,
        RoutingContext ctx,
        int maxStops = int.MaxValue,
        IReadOnlySet<int>? allowed = null)
    {
        var (candidates, indices) = Index(input);
        var evaluator = new ItineraryScheduleEvaluator(options);
        var comparer = Comparer(input, matrix);
        EvaluatedItinerarySchedule? best = null;
        var sequence = new List<int>();

        void Visit()
        {
            if (sequence.Count > 0
                && RouteState.Build(ctx, sequence) is { HasAllMandatory: true }
                && evaluator.Evaluate(input, sequence.Select(node => ctx.Nodes[node].Candidate).ToArray(), matrix, candidates, indices)
                    is { } schedule
                && (best is null || comparer.Compare(schedule, best) < 0))
            {
                best = schedule;
            }

            if (sequence.Count >= maxStops)
            {
                return;
            }

            for (var node = 0; node < ctx.Nodes.Count; node++)
            {
                if ((allowed is null || allowed.Contains(node)) && !sequence.Contains(node))
                {
                    sequence.Add(node);
                    Visit();
                    sequence.RemoveAt(sequence.Count - 1);
                }
            }
        }

        Visit();
        return best;
    }

    /// <summary>Product rule của heuristic cho đầu vào này.</summary>
    public static ScheduleGlobalComparator Comparer(GenerationInput input, RouteDurationMatrix matrix)
    {
        var (candidates, indices) = Index(input);
        return new ScheduleGlobalComparator(
            OptionalRouteOptimizer.SortOptionalsCanonical(candidates, input.MandatoryPoiIds, indices, matrix));
    }

    /// <summary>
    /// <paramref name="count"/> nút tùy chọn có thứ hạng chuẩn cao nhất: đúng miền giá trị của CSP khi đặt
    /// <c>MaxOptionalDomainSize</c>.
    /// </summary>
    public static int[] TopRankedOptionalNodes(GenerationInput input, RouteDurationMatrix matrix, RoutingContext ctx, int count)
    {
        var (candidates, indices) = Index(input);
        var rank = OptionalRouteOptimizer.SortOptionalsCanonical(candidates, input.MandatoryPoiIds, indices, matrix)
            .Select((candidate, index) => (candidate.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index);
        return ctx.OptionalNodes
            .OrderBy(node => rank[ctx.Nodes[node].Candidate.Id])
            .Take(count)
            .ToArray();
    }

    public static (GenerationCandidate[] Candidates, Dictionary<long, int> Indices) Index(GenerationInput input)
    {
        var candidates = input.Candidates.ToArray();
        return (candidates, candidates.Select((c, i) => (c.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index));
    }
}
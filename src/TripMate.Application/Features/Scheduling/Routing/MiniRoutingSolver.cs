using System.Diagnostics;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.Features.Scheduling.Routing;

public sealed record MiniRoutingResult(
    EvaluatedItinerarySchedule Schedule,
    EvaluatedItinerarySchedule InitialSchedule,
    MiniRoutingStatistics Statistics);

/// <summary>
/// Bộ giải lộ trình thu gọn cho TripMate, tái hiện có chọn lọc OR-Tools Routing Solver:
/// <list type="number">
/// <item>Lan truyền khung giờ Earliest/Latest để kiểm tra chèn/xóa O(1) (<see cref="RouteState"/>).</item>
/// <item>Hàm mục tiêu kiểu disjunction: phút di chuyển + phạt điểm tùy chọn bị bỏ.</item>
/// <item>Toán tử Or-opt và Exchange, bên cạnh 2-opt và Relocate sẵn có.</item>
/// <item>Toán tử Insert/Remove điểm tùy chọn.</item>
/// <item>Guided Local Search với giới hạn thời gian.</item>
/// </list>
/// Lời giải cuối luôn đi qua <see cref="ItineraryScheduleEvaluator"/> để chèn điểm nghỉ và kiểm tra
/// lại toàn bộ ràng buộc. Trả về null khi không tìm được lời giải hợp lệ, để bên gọi chuyển sang
/// <see cref="OptionalRouteOptimizer"/>.
/// </summary>
public sealed class MiniRoutingSolver(
    ItineraryScheduleEvaluator evaluator,
    SchedulingGenerationOptions? options = null,
    MiniRoutingOptions? routingOptions = null)
{
    private const int MaxMandatoryForPermutation = 6;

    private readonly ItineraryScheduleEvaluator _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly SchedulingGenerationOptions _options = options ?? new SchedulingGenerationOptions();
    private readonly MiniRoutingOptions _routingOptions = routingOptions ?? new MiniRoutingOptions();

    public MiniRoutingResult? Solve(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(matrixCandidates);
        ArgumentNullException.ThrowIfNull(candidateMatrixIndices);

        var clock = Stopwatch.StartNew();
        var stats = new MiniRoutingStatistics();
        var ctx = RoutingContext.Create(input, matrix, matrixCandidates, candidateMatrixIndices, _options, _routingOptions);

        if (ctx.MandatoryNodes.Count != input.MandatoryPoiIds.Count
            || ctx.MandatoryNodes.Count > MaxMandatoryForPermutation)
        {
            return null;
        }

        var initial = Construct(ctx, cancellationToken);
        if (initial is null)
        {
            return null;
        }

        stats.InitialObjective = initial.Objective;
        var search = new GuidedLocalSearch(ctx, _routingOptions, stats, clock, cancellationToken);
        var elite = search.Run(initial);

        var initialSchedule = initial.Count > 0 ? EvaluateState(ctx, initial, matrix, matrixCandidates, candidateMatrixIndices, cancellationToken) : null;

        foreach (var state in elite)
        {
            var schedule = EvaluateState(ctx, state, matrix, matrixCandidates, candidateMatrixIndices, cancellationToken);
            if (schedule is not null)
            {
                stats.BestObjective = state.Objective;
                stats.Elapsed = clock.Elapsed;
                return new MiniRoutingResult(schedule, initialSchedule ?? schedule, stats);
            }
        }

        stats.Elapsed = clock.Elapsed;
        return null;
    }

    /// <summary>
    /// Dựng lời giải ban đầu: thử mọi thứ tự của điểm bắt buộc (tối đa 6! = 720, mỗi thứ tự O(n)),
    /// rồi chèn rẻ nhất từng điểm tùy chọn theo thứ hạng, dùng kiểm tra O(1) cho mỗi vị trí.
    /// Một điểm tùy chọn chỉ được chèn khi phút di chuyển tăng thêm nhỏ hơn mức phạt bỏ qua nó.
    /// </summary>
    internal static RouteState? Construct(RoutingContext ctx, CancellationToken cancellationToken)
    {
        RouteState? best = null;
        foreach (var order in Permutations(ctx.MandatoryNodes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = RouteState.Build(ctx, order);
            if (state is not null && (best is null || state.Objective < best.Objective))
            {
                best = state;
            }
        }

        if (best is null)
        {
            return null;
        }

        foreach (var node in ctx.OptionalNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bestPosition = -1;
            var bestDelta = int.MaxValue;
            for (var position = 0; position <= best.Count; position++)
            {
                if (best.CanInsert(position, node, out var delta) && delta < bestDelta)
                {
                    bestDelta = delta;
                    bestPosition = position;
                }
            }

            if (bestPosition < 0 || bestDelta >= ctx.Nodes[node].SkipPenalty)
            {
                continue;
            }

            var next = RouteState.Build(ctx, best.WithInserted(bestPosition, node));
            if (next is not null)
            {
                best = next;
            }
        }

        return best;
    }

    private EvaluatedItinerarySchedule? EvaluateState(
        RoutingContext ctx,
        RouteState state,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        CancellationToken cancellationToken)
    {
        var sequence = state.Sequence.Select(node => ctx.Nodes[node].Candidate).ToArray();
        return _evaluator.Evaluate(ctx.Input, sequence, matrix, matrixCandidates, candidateMatrixIndices, cancellationToken);
    }

    private static IEnumerable<int[]> Permutations(IReadOnlyList<int> items)
    {
        if (items.Count == 0)
        {
            yield return [];
            yield break;
        }

        var working = items.ToArray();
        foreach (var permutation in Permute(working, 0))
        {
            yield return permutation;
        }
    }

    private static IEnumerable<int[]> Permute(int[] working, int index)
    {
        if (index == working.Length - 1)
        {
            yield return (int[])working.Clone();
            yield break;
        }

        for (var i = index; i < working.Length; i++)
        {
            (working[index], working[i]) = (working[i], working[index]);
            foreach (var permutation in Permute(working, index + 1))
            {
                yield return permutation;
            }

            (working[index], working[i]) = (working[i], working[index]);
        }
    }
}
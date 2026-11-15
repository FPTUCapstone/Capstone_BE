using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Product rule của <see cref="ScheduleGlobalComparator"/> áp dụng lên mô hình CSP (R1):
/// <list type="number">
/// <item>vector bao gồm điểm tùy chọn theo thứ hạng chuẩn (<see cref="OptionalRouteOptimizer.SortOptionalsCanonical"/>),
/// so sánh từ điển: có điểm xếp hạng cao hơn thì tốt hơn mọi tổ hợp điểm xếp hạng thấp hơn;</item>
/// <item>tổng phút di chuyển theo ma trận, tăng dần;</item>
/// <item>tổng thời lượng (mốc về đích), tăng dần;</item>
/// <item>dãy Id điểm tham quan, từ điển tăng dần.</item>
/// </list>
/// Vector bao gồm được lưu dạng bitset không giới hạn kích thước: hạng r nằm ở bit cao nhất trước, nên so sánh các
/// word như số không dấu cho đúng thứ tự từ điển. Lịch cuối cùng vẫn được chọn bằng chính
/// <see cref="ScheduleGlobalComparator"/> trên kết quả của <see cref="ItineraryScheduleEvaluator"/>.
/// </summary>
internal sealed class CspProductRule
{
    private const int BitsPerWord = 64;

    private readonly RoutingContext _ctx;
    private readonly Dictionary<long, int> _rankOfId;
    private readonly int[] _rankOfNode;

    private CspProductRule(
        RoutingContext ctx,
        IReadOnlyList<GenerationCandidate> canonicalOptionals,
        Dictionary<long, int> rankOfId,
        int[] rankOfNode)
    {
        _ctx = ctx;
        _rankOfId = rankOfId;
        _rankOfNode = rankOfNode;
        Words = Math.Max(1, (canonicalOptionals.Count + BitsPerWord - 1) / BitsPerWord);
        ScheduleComparer = new ScheduleGlobalComparator(canonicalOptionals);
    }

    /// <summary>Số word của một vector bao gồm.</summary>
    public int Words { get; }

    /// <summary>So sánh lịch đã qua evaluator, đúng như heuristic.</summary>
    public ScheduleGlobalComparator ScheduleComparer { get; }

    public static CspProductRule Create(
        RoutingContext ctx,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        RouteDurationMatrix matrix)
    {
        var canonical = OptionalRouteOptimizer.SortOptionalsCanonical(
            matrixCandidates, ctx.Input.MandatoryPoiIds, candidateMatrixIndices, matrix);
        var rankOfId = new Dictionary<long, int>(canonical.Count);
        for (var rank = 0; rank < canonical.Count; rank++)
        {
            rankOfId[canonical[rank].Id] = rank;
        }

        var rankOfNode = new int[ctx.Nodes.Count];
        for (var node = 0; node < ctx.Nodes.Count; node++)
        {
            rankOfNode[node] = ctx.Nodes[node].IsMandatory
                ? -1
                : rankOfId.GetValueOrDefault(ctx.Nodes[node].Candidate.Id, -1);
        }

        return new CspProductRule(ctx, canonical, rankOfId, rankOfNode);
    }

    /// <summary>
    /// Xây dựng quy tắc xếp hạng cho chế độ WeightedUtility: sắp xếp các điểm tùy chọn theo Base giảm dần,
    /// nếu bằng điểm thì hòa tie bằng Candidate.Id tăng dần.
    /// </summary>
    public static CspProductRule BuildRankRuleFromBase(
        RoutingContext ctx,
        IReadOnlyList<double> baseScores)
    {
        var canonical = ctx.OptionalNodes
            .OrderByDescending(node => baseScores[node])
            .ThenBy(node => ctx.Nodes[node].Candidate.Id)
            .Select(node => ctx.Nodes[node].Candidate)
            .ToList();

        var rankOfId = new Dictionary<long, int>(canonical.Count);
        for (var rank = 0; rank < canonical.Count; rank++)
        {
            rankOfId[canonical[rank].Id] = rank;
        }

        var rankOfNode = new int[ctx.Nodes.Count];
        for (var node = 0; node < ctx.Nodes.Count; node++)
        {
            rankOfNode[node] = ctx.Nodes[node].IsMandatory
                ? -1
                : rankOfId.GetValueOrDefault(ctx.Nodes[node].Candidate.Id, -1);
        }

        return new CspProductRule(ctx, canonical, rankOfId, rankOfNode);
    }

    /// <summary>Hạng chuẩn của một nút tùy chọn; -1 với điểm bắt buộc.</summary>
    public int RankOf(int node) => _rankOfNode[node];

    public ulong[] Inclusion(IReadOnlyList<int> nodes)
    {
        var bits = new ulong[Words];
        foreach (var node in nodes)
        {
            Set(bits, _rankOfNode[node]);
        }

        return bits;
    }

    public ulong[] Inclusion(IEnumerable<long> poiIds)
    {
        var bits = new ulong[Words];
        foreach (var id in poiIds)
        {
            Set(bits, _rankOfId.GetValueOrDefault(id, -1));
        }

        return bits;
    }

    public static void Set(ulong[] bits, int rank)
    {
        if (rank >= 0)
        {
            bits[rank / BitsPerWord] |= 1UL << (BitsPerWord - 1 - (rank % BitsPerWord));
        }
    }

    public static void Clear(ulong[] bits, int rank)
    {
        if (rank >= 0)
        {
            bits[rank / BitsPerWord] &= ~(1UL << (BitsPerWord - 1 - (rank % BitsPerWord)));
        }
    }

    /// <summary>Âm khi <paramref name="x"/> tốt hơn (có điểm xếp hạng cao hơn ở vị trí khác nhau đầu tiên).</summary>
    public static int CompareInclusion(ulong[] x, ulong[] y)
    {
        for (var i = 0; i < x.Length; i++)
        {
            if (x[i] != y[i])
            {
                return x[i] > y[i] ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>Product rule trên hai lời giải của mô hình; âm khi <paramref name="x"/> tốt hơn.</summary>
    public int Compare(RouteState x, RouteState y) =>
        Compare(x, Inclusion(x.Sequence), y, Inclusion(y.Sequence));

    /// <summary>Như <see cref="Compare(RouteState, RouteState)"/> với vector bao gồm đã tính sẵn của mỗi lời giải.</summary>
    public int Compare(RouteState x, ulong[] xInclusion, RouteState y, ulong[] yInclusion)
    {
        var inclusion = CompareInclusion(xInclusion, yInclusion);
        if (inclusion != 0)
        {
            return inclusion;
        }

        var travel = x.TravelMinutes.CompareTo(y.TravelMinutes);
        if (travel != 0)
        {
            return travel;
        }

        var finish = x.Finish.CompareTo(y.Finish);
        if (finish != 0)
        {
            return finish;
        }

        for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
        {
            var id = _ctx.Nodes[x.Sequence[i]].Candidate.Id.CompareTo(_ctx.Nodes[y.Sequence[i]].Candidate.Id);
            if (id != 0)
            {
                return id;
            }
        }

        return x.Count.CompareTo(y.Count);
    }
}
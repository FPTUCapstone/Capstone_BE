using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Triển khai IObjectiveKernel cho bài toán CSP theo thứ tự từ điển (Lexicographic / Product Rule).
/// Quản lý vector bao gồm (Inclusion), cận trên admissible (CanImprove), và incumbent dạng LexIncumbent.
/// </summary>
internal sealed class LexicographicKernel(CspProductRule rule, CspOptions options) : IObjectiveKernel
{
    private RoutingContext _ctx = null!;
    private SearchBuffers _buffers = null!;
    private SearchPrecomputed _pre = null!;
    private CspStatistics _stats = null!;

    private readonly ulong[] _inclusion = new ulong[rule.Words];
    private readonly ulong[] _optimistic = new ulong[rule.Words];
    private LexIncumbent? _incumbent;
    private readonly LexEliteOrdering _eliteOrdering = new(rule);

    public IEliteOrdering EliteOrdering => _eliteOrdering;

    public IComparer<EvaluatedItinerarySchedule> ScheduleComparer => rule.ScheduleComparer;

    public bool HasIncumbent => _incumbent is not null;

    public void Initialize(RoutingContext ctx, SearchBuffers buffers, SearchPrecomputed pre, CspStatistics statistics)
    {
        _ctx = ctx;
        _buffers = buffers;
        _pre = pre;
        _stats = statistics;
    }

    public IncumbentToken? AcceptInitial(EvaluatedItinerarySchedule? schedule)
    {
        if (schedule is null)
        {
            return null;
        }

        var nodeOf = new Dictionary<long, int>(_ctx.Nodes.Count);
        for (var node = 0; node < _ctx.Nodes.Count; node++)
        {
            nodeOf[_ctx.Nodes[node].Candidate.Id] = node;
        }

        var maxStops = options.MaxStops ?? int.MaxValue;
        if (schedule.VisitPoiIds.Count > maxStops
            || schedule.VisitPoiIds.Any(id => !nodeOf.TryGetValue(id, out var node) || !_pre.InInitialDomain[node]))
        {
            return null;
        }

        _stats.InitialIncumbentAccepted = true;
        return new LexIncumbent(rule.Inclusion(schedule.VisitPoiIds), schedule.TotalMatrixTravelMinutes);
    }

    public void ImportIncumbent(IncumbentToken? token)
    {
        _incumbent = token switch
        {
            null => null,
            LexIncumbent lex => lex, // Dùng chung tham chiếu đối tượng
            _ => throw new InvalidOperationException($"Không nhập được {token.GetType().Name} vào LexicographicKernel.")
        };
    }

    public IncumbentToken? ExportIncumbent() => _incumbent;

    public void OnPush(int depth, int value, int start, int fromMatrix)
    {
        CspProductRule.Set(_inclusion, rule.RankOf(value));
    }

    public void OnPop(int depth, int value)
    {
        CspProductRule.Clear(_inclusion, rule.RankOf(value));
    }

    public bool CanImprove(int[] domain, int domainCount, int depth, int departure, int current)
    {
        if (_incumbent is null)
        {
            return true;
        }

        long travel = _buffers.Travel[depth];
        var timeLeft = _ctx.Horizon - departure - _pre.MinReturnTravel;
        var budgetLeft = _ctx.Budget - _buffers.Cost[depth];
        var remainingMandatory = 0;
        foreach (var m in _ctx.MandatoryNodes)
        {
            if (!_buffers.Contains[m])
            {
                remainingMandatory++;
                travel += _pre.MinIncomingRaw[m];
                timeLeft -= _pre.MinIncomingTravel[m] + _ctx.Nodes[m].VisitMinutes;
                budgetLeft -= _ctx.Nodes[m].Cost;
            }
        }

        var maxStops = options.MaxStops ?? int.MaxValue;
        var slots = maxStops - depth - remainingMandatory;
        var optimistic = _optimistic;
        Array.Copy(_inclusion, optimistic, optimistic.Length);
        var finalReturn = _ctx.RawTravel(current, _ctx.EndMatrixIndex);
        for (var i = 0; i < domainCount; i++)
        {
            var v = domain[i];
            finalReturn = Math.Min(finalReturn, _pre.ReturnRaw[v]);
            var node = _ctx.Nodes[v];
            if (node.IsMandatory || slots <= 0)
            {
                continue;
            }

            var footprint = _pre.MinIncomingTravel[v] + node.VisitMinutes;
            if (footprint <= timeLeft && (budgetLeft is not { } left || node.Cost <= left))
            {
                CspProductRule.Set(optimistic, rule.RankOf(v));
                timeLeft -= footprint;
                budgetLeft -= node.Cost;
                slots--;
                travel += _pre.MinIncomingRaw[v];
            }
        }

        var comparison = CspProductRule.CompareInclusion(optimistic, _incumbent.Inclusion);
        if (comparison != 0)
        {
            return comparison < 0;
        }

        return travel + finalReturn <= _incumbent.Travel;
    }

    public long ValueScore(int value, int start, int current, int slack)
    {
        var node = _ctx.Nodes[value];
        var benefit = node.IsMandatory ? _pre.MandatoryWeight : node.SkipPenalty;
        return _ctx.RawTravel(current, node.MatrixIndex) - benefit;
    }

    public EliteKey PeekEliteKey(int depth, int departure, int current, int travel, int finish)
    {
        return new EliteKey(_inclusion, travel, finish);
    }

    public EliteKey SnapshotEliteKey(int depth, int departure, int current, int travel, int finish)
    {
        return new EliteKey((ulong[])_inclusion.Clone(), travel, finish);
    }

    public int CompareToIncumbent(int depth, int departure, int current, int travel)
    {
        return _incumbent?.CompareTo(_inclusion, travel) ?? -1;
    }

    public void AcceptIncumbent(int depth, int departure, int current, int travel)
    {
        Replace((ulong[])_inclusion.Clone(), travel);
    }

    public int CompareEvaluated(EvaluatedItinerarySchedule schedule)
    {
        return _incumbent?.CompareTo(_inclusion, schedule.TotalMatrixTravelMinutes) ?? -1;
    }

    public void AcceptEvaluated(EvaluatedItinerarySchedule schedule)
    {
        Replace((ulong[])_inclusion.Clone(), schedule.TotalMatrixTravelMinutes);
    }

    private void Replace(ulong[] inclusion, int travel)
    {
        if (_incumbent is null)
        {
            _incumbent = new LexIncumbent(inclusion, travel);
            return;
        }

        _incumbent.Inclusion = inclusion;
        _incumbent.Travel = travel;
    }
}

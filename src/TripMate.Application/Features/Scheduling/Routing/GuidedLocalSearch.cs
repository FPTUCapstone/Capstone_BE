using System.Diagnostics;

namespace TripMate.Application.Features.Scheduling.Routing;

/// <summary>Số liệu của một lần giải, dùng cho diagnostics và bảng so sánh trong báo cáo.</summary>
public sealed class MiniRoutingStatistics
{
    private readonly Dictionary<RoutingMoveKind, int> _acceptedMoves = new();

    public int MovesEvaluated { get; internal set; }

    public int LocalSearchRounds { get; internal set; }

    public int GlsIterations { get; internal set; }

    public long InitialObjective { get; internal set; }

    public long BestObjective { get; internal set; }

    public bool TimeLimitReached { get; internal set; }

    public TimeSpan Elapsed { get; internal set; }

    public IReadOnlyDictionary<RoutingMoveKind, int> AcceptedMoves => _acceptedMoves;

    internal void Accept(RoutingMoveKind kind) =>
        _acceptedMoves[kind] = _acceptedMoves.GetValueOrDefault(kind) + 1;
}

/// <summary>
/// Guided Local Search (kỹ thuật 5). Khi tìm kiếm cục bộ kẹt ở cực trị, GLS tăng mức phạt của
/// các cạnh có "tiện ích" cao nhất, tiện ích = phút di chuyển của cạnh / (1 + mức phạt hiện tại),
/// rồi tìm kiếm tiếp trên hàm mục tiêu tăng cường:
/// <c>Objective + lambda × tổng mức phạt các cạnh đang dùng</c>.
/// Lời giải tốt nhất luôn được chọn theo <see cref="RouteState.Objective"/> thật, không theo hàm tăng cường.
/// </summary>
public sealed class GuidedLocalSearch
{
    private readonly RoutingContext _ctx;
    private readonly MiniRoutingOptions _options;
    private readonly MiniRoutingStatistics _stats;
    private readonly Stopwatch _clock;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<(int From, int To), int> _arcPenalties = new();
    private readonly RouteEliteSet _elite;
    private double _lambda;

    public GuidedLocalSearch(
        RoutingContext ctx,
        MiniRoutingOptions options,
        MiniRoutingStatistics stats,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cancellationToken = cancellationToken;
        _elite = new RouteEliteSet(Math.Max(1, options.FinalCandidatesToValidate));
    }

    /// <summary>Chạy tìm kiếm và trả về các lời giải tốt nhất khác nhau, tốt nhất đứng đầu.</summary>
    public IReadOnlyList<RouteState> Run(RouteState initial)
    {
        ArgumentNullException.ThrowIfNull(initial);

        Offer(initial);
        var current = LocalSearch(initial, useAugmented: false);

        if (_options.EnableGuidedLocalSearch && current.Count > 0)
        {
            var arcCount = current.Count + 1;
            _lambda = _options.GlsLambdaFactor * current.Objective / arcCount;

            while (_stats.GlsIterations < _options.MaxGlsIterations && !TimeUp())
            {
                _stats.GlsIterations++;
                PenalizeMaxUtilityArcs(current);
                current = LocalSearch(current, useAugmented: true);
            }
        }

        _stats.BestObjective = _elite.Best?.Objective ?? initial.Objective;
        return _elite.Ordered;
    }

    private RouteState LocalSearch(RouteState start, bool useAugmented)
    {
        var current = start;
        while (!TimeUp())
        {
            _stats.LocalSearchRounds++;
            RouteState? best = null;
            var bestKind = default(RoutingMoveKind);
            var bestScore = Score(current, useAugmented);

            foreach (var move in LocalSearchOperators.Enumerate(_ctx, current, _options))
            {
                _stats.MovesEvaluated++;
                if ((_stats.MovesEvaluated & 255) == 0 && TimeUp())
                {
                    break;
                }

                var candidate = RouteState.Build(_ctx, move.Sequence);
                if (candidate is null || !candidate.HasAllMandatory)
                {
                    continue;
                }

                Offer(candidate);
                var score = Score(candidate, useAugmented);
                if (score < bestScore - 1e-9)
                {
                    best = candidate;
                    bestKind = move.Kind;
                    bestScore = score;
                }
            }

            if (best is null)
            {
                break;
            }

            _stats.Accept(bestKind);
            current = best;
        }

        return current;
    }

    private double Score(RouteState state, bool useAugmented)
    {
        if (!useAugmented)
        {
            return state.Objective;
        }

        var penaltySum = 0;
        foreach (var arc in state.Arcs())
        {
            penaltySum += _arcPenalties.GetValueOrDefault(arc);
        }

        return state.Objective + (_lambda * penaltySum);
    }

    private void PenalizeMaxUtilityArcs(RouteState state)
    {
        var maxUtility = double.MinValue;
        var arcs = state.Arcs().ToArray();
        foreach (var arc in arcs)
        {
            maxUtility = Math.Max(maxUtility, Utility(arc));
        }

        foreach (var arc in arcs)
        {
            if (Utility(arc) >= maxUtility - 1e-9)
            {
                _arcPenalties[arc] = _arcPenalties.GetValueOrDefault(arc) + 1;
            }
        }
    }

    private double Utility((int From, int To) arc) =>
        _ctx.RawTravel(arc.From, arc.To) / (1.0 + _arcPenalties.GetValueOrDefault(arc));

    private void Offer(RouteState state)
    {
        if (state.HasAllMandatory && state.Count > 0)
        {
            _elite.Offer(state);
        }
    }

    private bool TimeUp()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_clock.ElapsedMilliseconds < _options.TimeLimitMilliseconds)
        {
            return false;
        }

        _stats.TimeLimitReached = true;
        return true;
    }
}
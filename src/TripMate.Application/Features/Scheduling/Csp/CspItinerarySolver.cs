using System.Diagnostics;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>Số liệu của một lần giải CSP, dùng cho diagnostics và báo cáo.</summary>
public sealed class CspStatistics
{
    public int NodesExpanded { get; internal set; }

    /// <summary>Số lần gán Si = v bị loại vì vi phạm ràng buộc C1–C5.</summary>
    public int PrunedByConstraints { get; internal set; }

    /// <summary>Số nhánh bị cắt vì forward checking làm mất điểm bắt buộc (C6).</summary>
    public int PrunedByForwardChecking { get; internal set; }

    /// <summary>Số nhánh bị cắt vì cận dưới không tốt hơn lời giải tốt nhất (branch and bound).</summary>
    public int PrunedByBound { get; internal set; }

    public int SolutionsFound { get; internal set; }

    public int? NodesToFirstSolution { get; internal set; }

    /// <summary>
    /// True khi đã duyệt hết cây (không chạm giới hạn): lời giải tìm được là tối ưu theo hàm mục tiêu,
    /// hoặc nếu không có lời giải thì bài toán chắc chắn vô nghiệm.
    /// </summary>
    public bool SearchCompleted { get; internal set; }

    public bool NodeLimitReached { get; internal set; }

    public bool TimeLimitReached { get; internal set; }

    public long? BestObjective { get; internal set; }

    /// <summary>Mục tiêu của lời giải khởi đầu dùng làm cận trên ban đầu (nếu có).</summary>
    public long? InitialUpperBound { get; internal set; }

    public TimeSpan Elapsed { get; internal set; }
}

public sealed record CspResult(
    EvaluatedItinerarySchedule? Schedule,
    CspStatistics Statistics,
    IReadOnlyList<long> ConflictingMandatoryPoiIds)
{
    /// <summary>Đã duyệt hết cây mà không có lời giải: các điểm bắt buộc chắc chắn không xếp được.</summary>
    public bool ProvedInfeasible => Schedule is null && Statistics.SearchCompleted && Statistics.SolutionsFound == 0;
}

/// <summary>
/// Bộ giải CSP cho lập lịch TripMate.
/// <para><b>Biến:</b> các vị trí S1..Sk của lộ trình, gán lần lượt từ trái sang phải.</para>
/// <para><b>Miền giá trị:</b> điểm bắt buộc cộng N điểm tùy chọn xếp hạng cao nhất; dừng ở bất kỳ vị trí
/// nào cũng là kết thúc lộ trình (giá trị END).</para>
/// <para><b>Ràng buộc:</b> C1 mỗi POI một lần, C2 giờ mở cửa, C3 thời gian di chuyển, C4 ngân sách,
/// C5 kịp về đích, C6 đủ điểm bắt buộc.</para>
/// <para><b>Tìm kiếm:</b> backtracking; kiểm tra ràng buộc khi gán bằng <see cref="RouteState.CanInsert"/>;
/// forward checking loại khỏi miền mọi POI không còn khả năng xếp và quay lui ngay khi mất một điểm
/// bắt buộc; heuristic chọn giá trị ưu tiên điểm bắt buộc sắp đóng cửa (kiểu MRV) rồi điểm có lợi nhất
/// (kiểu LCV); branch and bound với cận dưới lạc quan của hàm mục tiêu.</para>
/// </summary>
public sealed class CspItinerarySolver(
    ItineraryScheduleEvaluator evaluator,
    SchedulingGenerationOptions? options = null,
    CspOptions? cspOptions = null)
{
    private readonly ItineraryScheduleEvaluator _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly SchedulingGenerationOptions _options = options ?? new SchedulingGenerationOptions();
    private readonly CspOptions _cspOptions = cspOptions ?? options?.Csp ?? new CspOptions();

    public CspResult Solve(
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
        var ctx = RoutingContext.Create(input, matrix, matrixCandidates, candidateMatrixIndices, _options, _options.MiniRouting);
        var search = new SearchRun(ctx, _cspOptions, clock, cancellationToken);

        if (ctx.MandatoryNodes.Count != input.MandatoryPoiIds.Count)
        {
            search.Statistics.Elapsed = clock.Elapsed;
            return new CspResult(null, search.Statistics, []);
        }

        search.Run();

        if (_cspOptions.PolishWithLocalSearch && search.Elite.Best is { } bestCsp)
        {
            var polishOptions = new MiniRoutingOptions
            {
                MaxSkipPenaltyMinutes = _options.MiniRouting.MaxSkipPenaltyMinutes,
                MaxGlsIterations = _options.MiniRouting.MaxGlsIterations,
                TimeLimitMilliseconds = _cspOptions.TimeLimitMilliseconds,
                FinalCandidatesToValidate = _cspOptions.FinalCandidatesToValidate,
            };
            var polished = new GuidedLocalSearch(ctx, polishOptions, new MiniRoutingStatistics(), clock, cancellationToken)
                .Run(bestCsp);
            foreach (var state in polished)
            {
                search.Elite.Offer(state);
            }
        }

        foreach (var state in search.Elite.Ordered)
        {
            var sequence = state.Sequence.Select(node => ctx.Nodes[node].Candidate).ToArray();
            var schedule = _evaluator.Evaluate(input, sequence, matrix, matrixCandidates, candidateMatrixIndices, cancellationToken);
            if (schedule is not null)
            {
                search.Statistics.BestObjective = state.Objective;
                search.Statistics.Elapsed = clock.Elapsed;
                return new CspResult(schedule, search.Statistics, []);
            }
        }

        search.Statistics.Elapsed = clock.Elapsed;
        return new CspResult(null, search.Statistics, search.ConflictingMandatoryPoiIds());
    }

    /// <summary>Trạng thái của một lần duyệt cây tìm kiếm.</summary>
    private sealed class SearchRun
    {
        private const int UrgentSlackMinutes = 60;

        private readonly RoutingContext _ctx;
        private readonly CspOptions _options;
        private readonly Stopwatch _clock;
        private readonly CancellationToken _cancellationToken;
        private readonly int[] _initialDomain;
        private readonly int[] _minIncomingTravel;
        private readonly int[] _minIncomingRaw;
        private readonly int[] _latestPossibleStart;
        private readonly int[] _returnRaw;
        private readonly int _minReturnTravel;
        private readonly int _mandatoryWeight;
        private readonly Dictionary<int, int> _wipeouts = new();
        private long _bestObjective = long.MaxValue;
        private bool _aborted;

        public SearchRun(RoutingContext ctx, CspOptions options, Stopwatch clock, CancellationToken cancellationToken)
        {
            _ctx = ctx;
            _options = options;
            _clock = clock;
            _cancellationToken = cancellationToken;
            Elite = new RouteEliteSet(Math.Max(1, options.FinalCandidatesToValidate));

            // Miền giá trị ban đầu: mọi điểm bắt buộc + N điểm tùy chọn xếp hạng cao nhất.
            _initialDomain = ctx.MandatoryNodes
                .Concat(ctx.OptionalNodes.Take(Math.Max(0, options.MaxOptionalDomainSize)))
                .ToArray();

            var nodeCount = ctx.Nodes.Count;
            _minIncomingTravel = new int[nodeCount];
            _minIncomingRaw = new int[nodeCount];
            _latestPossibleStart = new int[nodeCount];
            _returnRaw = new int[nodeCount];
            _minReturnTravel = int.MaxValue;

            foreach (var v in _initialDomain)
            {
                var target = ctx.Nodes[v].MatrixIndex;
                var minTravel = ctx.Travel(ctx.StartMatrixIndex, target);
                var minRaw = ctx.RawTravel(ctx.StartMatrixIndex, target);
                foreach (var u in _initialDomain)
                {
                    if (u == v)
                    {
                        continue;
                    }

                    var source = ctx.Nodes[u].MatrixIndex;
                    minTravel = Math.Min(minTravel, ctx.Travel(source, target));
                    minRaw = Math.Min(minRaw, ctx.RawTravel(source, target));
                }

                _minIncomingTravel[v] = minTravel;
                _minIncomingRaw[v] = minRaw;
                _returnRaw[v] = ctx.RawTravel(target, ctx.EndMatrixIndex);
                _minReturnTravel = Math.Min(_minReturnTravel, ctx.ReturnTravel(target));
            }

            if (_minReturnTravel == int.MaxValue)
            {
                _minReturnTravel = ctx.ReturnTravel(ctx.StartMatrixIndex);
            }

            // Ma trận đường đi thật không thỏa bất đẳng thức tam giác: về đích qua một điểm khác có thể
            // nhanh hơn về thẳng. Thời gian tối thiểu từ lúc rời v tới lúc về đích vì vậy là
            // min(về thẳng, đi tới điểm gần nhất + thời gian tham quan ngắn nhất + chặng về đích ngắn nhất).
            var minVisit = _initialDomain.Select(i => ctx.Nodes[i].VisitMinutes).DefaultIfEmpty(0).Min();
            foreach (var v in _initialDomain)
            {
                var source = ctx.Nodes[v].MatrixIndex;
                var minOutgoing = int.MaxValue;
                foreach (var u in _initialDomain)
                {
                    if (u != v)
                    {
                        minOutgoing = Math.Min(minOutgoing, ctx.Travel(source, ctx.Nodes[u].MatrixIndex));
                    }
                }

                var minExit = ctx.ReturnTravel(source);
                if (minOutgoing != int.MaxValue)
                {
                    minExit = Math.Min(minExit, minOutgoing + minVisit + _minReturnTravel);
                }

                // Giờ bắt đầu muộn nhất mà vẫn có thể tham quan xong và về đích, bất kể đi từ đâu tới.
                _latestPossibleStart[v] = ctx.LatestStart(v, ctx.Horizon - minExit - ctx.Nodes[v].VisitMinutes);
            }

            var maxPenalty = ctx.OptionalNodes.Select(i => ctx.Nodes[i].SkipPenalty).DefaultIfEmpty(1).Max();
            _mandatoryWeight = 2 * Math.Max(1, maxPenalty);
        }

        public CspStatistics Statistics { get; } = new();

        public RouteEliteSet Elite { get; }

        public void Run()
        {
            var root = RouteState.Build(_ctx, []);
            if (root is null)
            {
                Statistics.SearchCompleted = true;
                return;
            }

            // Forward checking tại gốc: điểm nào không bao giờ xếp được thì loại ngay.
            var domain = new List<int>(_initialDomain.Length);
            foreach (var v in _initialDomain)
            {
                if (_latestPossibleStart[v] != RoutingContext.NoLatestStart
                    && (_ctx.Budget is not { } budget || _ctx.Nodes[v].Cost <= budget))
                {
                    domain.Add(v);
                }
                else if (_ctx.Nodes[v].IsMandatory)
                {
                    RecordWipeout(v);
                    Statistics.PrunedByForwardChecking++;
                    Statistics.SearchCompleted = true;
                    return;
                }
            }

            if (!RemainingMandatoryFit(root))
            {
                Statistics.PrunedByForwardChecking++;
                Statistics.SearchCompleted = true;
                return;
            }

            // Cận trên ban đầu cho branch and bound: một lời giải chèn rẻ nhất dựng rất nhanh. Có cận sớm
            // thì những nhánh tệ bị cắt ngay từ đầu thay vì phải chờ DFS tự tìm ra lời giải đầu tiên.
            if (_options.UseInitialIncumbent
                && MiniRoutingSolver.Construct(_ctx, _cancellationToken) is { Count: > 0, HasAllMandatory: true } incumbent)
            {
                Elite.Offer(incumbent);
                _bestObjective = incumbent.Objective;
                Statistics.InitialUpperBound = incumbent.Objective;
                Statistics.SolutionsFound++;
            }

            Search(root, domain);
            Statistics.SearchCompleted = !_aborted;
        }

        public IReadOnlyList<long> ConflictingMandatoryPoiIds() =>
            _wipeouts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => _ctx.Nodes[pair.Key].Candidate.Id)
                .Take(3)
                .Select(pair => _ctx.Nodes[pair.Key].Candidate.Id)
                .ToArray();

        private void Search(RouteState state, List<int> domain)
        {
            Statistics.NodesExpanded++;
            if (LimitReached())
            {
                return;
            }

            // Mọi trạng thái đã có đủ điểm bắt buộc là một lời giải hợp lệ (gán END tại đây).
            if (state.Count > 0 && state.HasAllMandatory)
            {
                Statistics.SolutionsFound++;
                Statistics.NodesToFirstSolution ??= Statistics.NodesExpanded;
                Elite.Offer(state);
                _bestObjective = Math.Min(_bestObjective, state.Objective);
            }

            if (state.Count >= _options.MaxStops || domain.Count == 0)
            {
                return;
            }

            // Branch and bound: nhánh này không thể tốt hơn lời giải tốt nhất thì cắt.
            if (_bestObjective != long.MaxValue && LowerBound(state, domain) >= _bestObjective)
            {
                Statistics.PrunedByBound++;
                return;
            }

            foreach (var value in OrderValues(state, domain))
            {
                if (_aborted)
                {
                    return;
                }

                // Gán S(k+1) = value và kiểm tra ràng buộc C1–C5 trong O(1).
                if (!state.CanInsert(state.Count, value, out _))
                {
                    Statistics.PrunedByConstraints++;
                    continue;
                }

                var child = RouteState.Build(_ctx, state.WithInserted(state.Count, value));
                if (child is null)
                {
                    Statistics.PrunedByConstraints++;
                    continue;
                }

                // Forward checking: thu hẹp miền; mất điểm bắt buộc thì quay lui ngay.
                var childDomain = ForwardCheck(child, domain, value, out var wipedMandatory);
                if (childDomain is null)
                {
                    RecordWipeout(wipedMandatory);
                    Statistics.PrunedByForwardChecking++;
                    continue;
                }

                if (!RemainingMandatoryFit(child))
                {
                    Statistics.PrunedByForwardChecking++;
                    continue;
                }

                Search(child, childDomain);
            }
        }

        /// <summary>
        /// Giữ lại trong miền những POI còn có thể xếp ở một vị trí nào đó phía sau. Một POI bị loại khi
        /// ngay cả giờ đến sớm nhất có thể (đi thẳng tới, hoặc từ điểm gần nó nhất) cũng đã muộn hơn giờ
        /// bắt đầu muộn nhất của nó, hoặc khi phần ngân sách còn lại không đủ. Cách loại này không bao giờ
        /// bỏ sót lời giải. Trả về null khi một điểm bắt buộc bị loại.
        /// </summary>
        private List<int>? ForwardCheck(RouteState child, List<int> domain, int placed, out int wipedMandatory)
        {
            wipedMandatory = -1;
            var last = child.Sequence[^1];
            var departure = child.Earliest[child.Count - 1] + _ctx.Nodes[last].VisitMinutes;
            var current = _ctx.Nodes[last].MatrixIndex;
            var remainingBudget = _ctx.Budget - child.Cost;
            var result = new List<int>(domain.Count);

            foreach (var v in domain)
            {
                if (v == placed)
                {
                    continue;
                }

                var node = _ctx.Nodes[v];
                var keep = remainingBudget is not { } budgetLeft || node.Cost <= budgetLeft;
                if (keep)
                {
                    var earliestArrival = departure + Math.Min(_ctx.Travel(current, node.MatrixIndex), _minIncomingTravel[v]);
                    keep = earliestArrival <= _latestPossibleStart[v];
                }

                if (keep)
                {
                    result.Add(v);
                }
                else if (node.IsMandatory)
                {
                    wipedMandatory = v;
                    return null;
                }
            }

            return result;
        }

        /// <summary>
        /// Kiểm tra nới lỏng: các điểm bắt buộc chưa xếp, mỗi điểm tốn ít nhất (đường đến ngắn nhất + thời gian
        /// tham quan), cộng chặng về đích ngắn nhất, phải còn vừa thời gian; tổng chi phí phải vừa ngân sách.
        /// </summary>
        private bool RemainingMandatoryFit(RouteState state)
        {
            var departure = state.Count == 0
                ? 0
                : state.Earliest[state.Count - 1] + _ctx.Nodes[state.Sequence[^1]].VisitMinutes;
            var time = departure;
            var cost = state.Cost;
            var remaining = 0;

            foreach (var m in _ctx.MandatoryNodes)
            {
                if (state.Contains(m))
                {
                    continue;
                }

                remaining++;
                time += _minIncomingTravel[m] + _ctx.Nodes[m].VisitMinutes;
                cost += _ctx.Nodes[m].Cost;
            }

            if (remaining == 0)
            {
                return true;
            }

            return time + _minReturnTravel <= _ctx.Horizon
                && (_ctx.Budget is not { } budget || cost <= budget);
        }

        /// <summary>
        /// Cận dưới lạc quan của mọi lời giải mở rộng từ <paramref name="state"/>: phút đã đi (bỏ chặng về đích),
        /// cộng đường đến ngắn nhất của từng điểm bắt buộc còn thiếu, cộng chặng về đích ngắn nhất, cộng mức phạt
        /// của những điểm tùy chọn không thể thu hồi (đã rời miền hoặc không còn vị trí trống).
        /// </summary>
        private long LowerBound(RouteState state, List<int> domain)
        {
            var lastMatrix = state.Count == 0 ? _ctx.StartMatrixIndex : _ctx.Nodes[state.Sequence[^1]].MatrixIndex;
            long bound = state.TravelMinutes - _ctx.RawTravel(lastMatrix, _ctx.EndMatrixIndex);

            var remainingMandatory = 0;
            foreach (var m in _ctx.MandatoryNodes)
            {
                if (!state.Contains(m))
                {
                    remainingMandatory++;
                    bound += _minIncomingRaw[m];
                }
            }

            var finalReturn = _ctx.RawTravel(lastMatrix, _ctx.EndMatrixIndex);
            foreach (var v in domain)
            {
                finalReturn = Math.Min(finalReturn, _returnRaw[v]);
            }

            bound += finalReturn;

            // Mỗi điểm tùy chọn trong miền: hoặc bị bỏ (trả mức phạt p), hoặc được thêm (tốn ít nhất
            // đường đến ngắn nhất d). Phần "lợi" khi thêm là max(0, p - d). Số điểm thêm được bị chặn bởi
            // số vị trí còn trống và bởi thời gian còn lại; cận lấy những phần lợi lớn nhất trong giới hạn đó.
            var departure = state.Count == 0
                ? 0
                : state.Earliest[state.Count - 1] + _ctx.Nodes[state.Sequence[^1]].VisitMinutes;
            var timeLeft = _ctx.Horizon - departure - _minReturnTravel;
            foreach (var m in _ctx.MandatoryNodes)
            {
                if (!state.Contains(m))
                {
                    timeLeft -= _minIncomingTravel[m] + _ctx.Nodes[m].VisitMinutes;
                }
            }

            var domainPenalty = 0;
            var gains = new List<int>(domain.Count);
            var footprints = new List<int>(domain.Count);
            foreach (var v in domain)
            {
                var node = _ctx.Nodes[v];
                if (node.IsMandatory)
                {
                    continue;
                }

                domainPenalty += node.SkipPenalty;
                gains.Add(Math.Max(0, node.SkipPenalty - _minIncomingRaw[v]));
                footprints.Add(_minIncomingTravel[v] + node.VisitMinutes);
            }

            footprints.Sort();
            var slotsByTime = 0;
            var used = 0;
            foreach (var footprint in footprints)
            {
                if (used + footprint > timeLeft)
                {
                    break;
                }

                used += footprint;
                slotsByTime++;
            }

            var slots = Math.Min(_options.MaxStops - state.Count - remainingMandatory, slotsByTime);
            gains.Sort((a, b) => b.CompareTo(a));
            var bestGain = 0;
            for (var i = 0; i < Math.Min(Math.Max(0, slots), gains.Count); i++)
            {
                bestGain += gains[i];
            }

            var outsideDomainPenalty = _ctx.TotalOptionalPenalty - state.VisitedOptionalPenalty - domainPenalty;
            return bound + outsideDomainPenalty + domainPenalty - bestGain;
        }

        /// <summary>
        /// Thứ tự thử giá trị: điểm bắt buộc sắp hết giờ (độ trễ cho phép dưới 60 phút) trước, theo độ trễ
        /// tăng dần (kiểu MRV); sau đó theo "phút đi thêm trừ lợi ích" tăng dần (kiểu LCV), lợi ích của điểm
        /// bắt buộc lớn hơn mọi điểm tùy chọn. Giá trị không thể bắt đầu đúng giờ bị loại ngay.
        /// </summary>
        private IEnumerable<int> OrderValues(RouteState state, List<int> domain)
        {
            var departure = state.Count == 0
                ? 0
                : state.Earliest[state.Count - 1] + _ctx.Nodes[state.Sequence[^1]].VisitMinutes;
            var current = state.Count == 0 ? _ctx.StartMatrixIndex : _ctx.Nodes[state.Sequence[^1]].MatrixIndex;
            var ranked = new List<(int Value, bool Urgent, int Slack, int Score, long Id)>(domain.Count);

            foreach (var v in domain)
            {
                var node = _ctx.Nodes[v];
                var start = _ctx.StartAt(v, departure + _ctx.Travel(current, node.MatrixIndex));
                if (start == RoutingContext.Infeasible)
                {
                    Statistics.PrunedByConstraints++;
                    continue;
                }

                var slack = _latestPossibleStart[v] - start;
                var benefit = node.IsMandatory ? _mandatoryWeight : node.SkipPenalty;
                var score = _ctx.RawTravel(current, node.MatrixIndex) - benefit;
                ranked.Add((v, node.IsMandatory && slack < UrgentSlackMinutes, slack, score, node.Candidate.Id));
            }

            return ranked
                .OrderByDescending(item => item.Urgent)
                .ThenBy(item => item.Urgent ? item.Slack : 0)
                .ThenBy(item => item.Score)
                .ThenBy(item => item.Id)
                .Select(item => item.Value)
                .ToArray();
        }

        private void RecordWipeout(int mandatoryNode) =>
            _wipeouts[mandatoryNode] = _wipeouts.GetValueOrDefault(mandatoryNode) + 1;

        private bool LimitReached()
        {
            if (_aborted)
            {
                return true;
            }

            if (Statistics.NodesExpanded > _options.MaxNodes)
            {
                Statistics.NodeLimitReached = true;
                _aborted = true;
            }
            else if ((Statistics.NodesExpanded & 255) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (_clock.ElapsedMilliseconds >= _options.TimeLimitMilliseconds)
                {
                    Statistics.TimeLimitReached = true;
                    _aborted = true;
                }
            }

            return _aborted;
        }
    }
}
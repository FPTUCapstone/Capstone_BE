using System.Diagnostics;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>Số liệu của một lần giải CSP, dùng cho diagnostics và báo cáo.</summary>
public sealed class CspStatistics
{
    public int NodesExpanded { get; internal set; }

    /// <summary>Số lần gán Si = v bị loại vì vi phạm ràng buộc C1–C5.</summary>
    public int PrunedByConstraints { get; internal set; }

    /// <summary>Số nhánh bị cắt vì forward checking làm mất điểm bắt buộc (C6).</summary>
    public int PrunedByForwardChecking { get; internal set; }

    /// <summary>Số nhánh bị cắt vì cận lạc quan theo product rule không tốt hơn incumbent (branch and bound).</summary>
    public int PrunedByBound { get; internal set; }

    public int SolutionsFound { get; internal set; }

    public int? NodesToFirstSolution { get; internal set; }

    /// <summary>
    /// True khi lần duyệt cuối (mô hình nới lỏng nếu có) đã duyệt hết cây, không chạm giới hạn. Khi đó, nếu không có
    /// lời giải thì bài toán chắc chắn vô nghiệm. Lời giải tìm được chỉ chắc chắn tối ưu theo product rule khi lịch
    /// không có điểm nghỉ (<see cref="RestPreference.None"/>, hoặc Auto dưới 300 phút): mô hình CSP không tính
    /// điểm nghỉ mà <see cref="ItineraryScheduleEvaluator"/> chèn vào.
    /// </summary>
    public bool SearchCompleted { get; internal set; }

    public bool NodeLimitReached { get; internal set; }

    public bool TimeLimitReached { get; internal set; }

    /// <summary>
    /// True khi lịch heuristic được truyền vào nằm trong giới hạn CSP (MaxStops, miền giá trị) và được dùng làm incumbent
    /// ban đầu (R1a). Lịch ngoài giới hạn không được dùng, giống như mọi lời giải khác của CSP.
    /// </summary>
    public bool InitialIncumbentAccepted { get; internal set; }

    /// <summary>True khi không lời giải nào của CSP tốt hơn incumbent ban đầu, nên chính lịch heuristic được trả về.</summary>
    public bool ReturnedInitialIncumbent { get; internal set; }

    /// <summary>Số lời giải đã đưa qua <see cref="ItineraryScheduleEvaluator"/> (trong lúc duyệt và ở bước chọn cuối).</summary>
    public int EvaluatorCalls { get; internal set; }

    public TimeSpan Elapsed { get; internal set; }

    /// <summary>
    /// True khi bộ giải đã chạy thêm mô hình nới lỏng (không dự phòng thời gian nghỉ) sau mô hình có dự phòng. Hai
    /// lần chạy dùng chung một giới hạn <see cref="CspOptions.MaxNodes"/>; các số liệu đếm là tổng của cả hai lần.
    /// </summary>
    public bool UsedRelaxedRestModel { get; internal set; }

    /// <summary>True khi lịch được trả về đến từ mô hình nới lỏng.</summary>
    public bool ChoseRelaxedRestModel { get; internal set; }

    internal void AddEarlierRun(CspStatistics earlier)
    {
        NodesExpanded += earlier.NodesExpanded;
        PrunedByConstraints += earlier.PrunedByConstraints;
        PrunedByForwardChecking += earlier.PrunedByForwardChecking;
        PrunedByBound += earlier.PrunedByBound;
        SolutionsFound += earlier.SolutionsFound;
        NodesToFirstSolution = earlier.NodesToFirstSolution ?? (NodesToFirstSolution + earlier.NodesExpanded);
        NodeLimitReached |= earlier.NodeLimitReached;
        TimeLimitReached |= earlier.TimeLimitReached;
        InitialIncumbentAccepted |= earlier.InitialIncumbentAccepted;
        UsedRelaxedRestModel = true;
    }
}

public sealed record CspResult(
    EvaluatedItinerarySchedule? Schedule,
    CspStatistics Statistics,
    IReadOnlyList<long> ConflictingMandatoryPoiIds)
{
    /// <summary>
    /// Đã duyệt hết cây của mô hình nới lỏng (không dự phòng thời gian nghỉ) mà không có lời giải. Bên gọi vẫn nên
    /// thử thuật toán khác trước khi báo lỗi cho người dùng: mô hình không tính các điểm nghỉ mà
    /// <see cref="ItineraryScheduleEvaluator"/> chèn vào.
    /// </summary>
    public bool ProvedInfeasible => Schedule is null && Statistics.SearchCompleted && Statistics.SolutionsFound == 0;
}

/// <summary>
/// Bộ giải CSP cho lập lịch TripMate.
/// <para><b>Biến:</b> các vị trí S1..Sk của lộ trình, gán lần lượt từ trái sang phải.</para>
/// <para><b>Miền giá trị:</b> điểm bắt buộc cộng N điểm tùy chọn có thứ hạng chuẩn cao nhất (mặc định mọi điểm); dừng ở
/// bất kỳ vị trí nào cũng là kết thúc lộ trình (giá trị END).</para>
/// <para><b>Ràng buộc:</b> C1 mỗi POI một lần, C2 giờ mở cửa, C3 thời gian di chuyển, C4 ngân sách,
/// C5 kịp về đích, C6 đủ điểm bắt buộc.</para>
/// <para><b>Mục tiêu (R1):</b> product rule của <see cref="ScheduleGlobalComparator"/> (<see cref="CspProductRule"/>):
/// điểm tùy chọn xếp hạng cao hơn luôn thắng phút di chuyển.</para>
/// <para><b>Tìm kiếm:</b> backtracking trên một lộ trình đang xét dùng chung (không cấp phát ở mỗi nút): C1–C4 kiểm tra
/// khi gán, C5 chỉ theo cận dưới của đường về (có thể về qua điểm khác) và được kiểm tra đầy đủ khi gán END;
/// forward checking loại khỏi miền mọi POI không còn khả năng xếp và quay lui ngay khi mất một điểm
/// bắt buộc; heuristic chọn giá trị ưu tiên điểm bắt buộc sắp đóng cửa (kiểu MRV) rồi điểm có lợi nhất
/// (kiểu LCV); branch and bound với cận lạc quan theo product rule, bắt đầu từ lịch heuristic (R1a).</para>
/// </summary>
public sealed class CspItinerarySolver(
    ItineraryScheduleEvaluator evaluator,
    SchedulingGenerationOptions? options = null,
    CspOptions? cspOptions = null)
{
    /// <summary>
    /// Số lần đưa lời giải qua evaluator trong lúc duyệt (chỉ khi có điểm nghỉ) để siết incumbent. Hết lượt thì
    /// incumbent chỉ còn được siết bởi lịch đã kiểm tra trước đó.
    /// </summary>
    private const int MaxInSearchEvaluations = 256;

    private readonly ItineraryScheduleEvaluator _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly SchedulingGenerationOptions _options = options ?? new SchedulingGenerationOptions();
    private readonly CspOptions _cspOptions = cspOptions ?? options?.Csp ?? new CspOptions();

    public CspResult Solve(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        CancellationToken cancellationToken = default) =>
        Solve(input, matrix, matrixCandidates, candidateMatrixIndices, initialIncumbent: null, cancellationToken);

    /// <param name="initialIncumbent">
    /// Lịch của heuristic cho cùng đầu vào (R1a). Khi nằm trong giới hạn CSP, nó là incumbent ban đầu của branch and
    /// bound và là một ứng viên ở bước chọn cuối, nên kết quả không bao giờ kém nó theo product rule, kể cả khi chạm giới
    /// hạn số nút. Null: duyệt không có incumbent.
    /// </param>
    public CspResult Solve(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        EvaluatedItinerarySchedule? initialIncumbent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(matrixCandidates);
        ArgumentNullException.ThrowIfNull(candidateMatrixIndices);

        var clock = Stopwatch.StartNew();
        var budget = new SearchBudget(_cspOptions.MaxNodes);
        var ctx = RoutingContext.Create(input, matrix, matrixCandidates, candidateMatrixIndices, _options, _options.MiniRouting);
        var rule = CspProductRule.Create(ctx, matrixCandidates, candidateMatrixIndices, matrix);

        // Khi có điểm nghỉ, lịch thật khác mô hình: chỉ lời giải đã qua evaluator mới được siết incumbent.
        var restApplies = input.RestPreference == RestPreference.Frequent
            || (input.RestPreference == RestPreference.Auto && input.AvailableMinutes >= 300);
        var evaluate = (RoutingContext context, IReadOnlyList<int> nodes, CspStatistics statistics) =>
        {
            statistics.EvaluatorCalls++;
            var sequence = nodes.Select(node => context.Nodes[node].Candidate).ToArray();
            return _evaluator.Evaluate(input, sequence, matrix, matrixCandidates, candidateMatrixIndices, cancellationToken);
        };

        var search = new SearchRun(ctx, rule, _cspOptions, clock, budget, restApplies, evaluate, cancellationToken);
        if (ctx.MandatoryNodes.Count != input.MandatoryPoiIds.Count)
        {
            search.Statistics.Elapsed = clock.Elapsed;
            return new CspResult(null, search.Statistics, []);
        }

        var incumbent = search.AcceptIncumbent(initialIncumbent);
        var candidates = new List<(EvaluatedItinerarySchedule Schedule, bool Relaxed)>();
        if (incumbent is not null)
        {
            candidates.Add((initialIncumbent!, false));
        }

        search.Run(incumbent);
        if (search.Statistics.TimeLimitReached)
        {
            return DiscardTimedOutResult(search, clock);
        }

        Polish(search, clock, cancellationToken);
        candidates.AddRange(search.EvaluatedSolutions.Select(schedule => (schedule, false)));
        candidates.AddRange(EvaluateElite(search, stopAtFirstValid: !restApplies, evaluate).Select(schedule => (schedule, false)));

        // Dự phòng thời gian nghỉ làm mô hình chặt hơn thực tế: nó có thể loại mất lịch tốt hơn, và "không có lời
        // giải" ở đây chưa phải là vô nghiệm. Luôn chạy thêm mô hình nới lỏng (không dự phòng) với phần node budget
        // còn lại, tiếp tục từ incumbent hiện có. Chỉ kết quả của mô hình nới lỏng mới được dùng để kết luận vô nghiệm.
        if (ctx.Horizon < input.AvailableMinutes)
        {
            var relaxedCtx = RoutingContext.Create(
                input, matrix, matrixCandidates, candidateMatrixIndices, _options, _options.MiniRouting, reserveRestTime: false);
            var relaxed = new SearchRun(relaxedCtx, rule, _cspOptions, clock, budget, restApplies, evaluate, cancellationToken);
            relaxed.Statistics.EvaluatorCalls = search.Statistics.EvaluatorCalls;
            relaxed.Run(search.Incumbent);
            if (relaxed.Statistics.TimeLimitReached)
            {
                relaxed.Statistics.AddEarlierRun(search.Statistics);
                return DiscardTimedOutResult(relaxed, clock);
            }

            Polish(relaxed, clock, cancellationToken);
            candidates.AddRange(relaxed.EvaluatedSolutions.Select(schedule => (schedule, true)));
            candidates.AddRange(EvaluateElite(relaxed, stopAtFirstValid: false, evaluate).Select(schedule => (schedule, true)));
            relaxed.Statistics.AddEarlierRun(search.Statistics);
            search = relaxed;
        }

        search.Statistics.Elapsed = clock.Elapsed;
        if (candidates.Count == 0)
        {
            return new CspResult(null, search.Statistics, search.ConflictingMandatoryPoiIds());
        }

        // Chọn lịch bằng đúng quy tắc của heuristic trên kết quả của evaluator; bằng nhau thì giữ lịch đứng trước.
        var best = candidates[0];
        foreach (var candidate in candidates.Skip(1))
        {
            if (rule.ScheduleComparer.Compare(candidate.Schedule, best.Schedule) < 0)
            {
                best = candidate;
            }
        }

        search.Statistics.ChoseRelaxedRestModel = best.Relaxed;
        search.Statistics.ReturnedInitialIncumbent = ReferenceEquals(best.Schedule, initialIncumbent);
        return new CspResult(best.Schedule, search.Statistics, []);
    }

    /// <summary>
    /// R5: lưới an toàn thời gian bị chạm thì kết quả CSP dở dang phụ thuộc tốc độ máy, nên bị bỏ hẳn, kể cả lịch heuristic
    /// đang là incumbent. Bên gọi trả lịch heuristic nguyên vẹn (<see cref="CspStatistics.TimeLimitReached"/>), để cùng
    /// một đầu vào luôn cho cùng một lịch trình.
    /// </summary>
    private static CspResult DiscardTimedOutResult(SearchRun search, Stopwatch clock)
    {
        search.Statistics.Elapsed = clock.Elapsed;
        return new CspResult(null, search.Statistics, []);
    }

    private void Polish(SearchRun search, Stopwatch clock, CancellationToken cancellationToken)
    {
        if (!_cspOptions.PolishWithLocalSearch || search.Elite.Best is not { } bestCsp)
        {
            return;
        }

        var polishOptions = new MiniRoutingOptions
        {
            MaxSkipPenaltyMinutes = _options.MiniRouting.MaxSkipPenaltyMinutes,
            MaxGlsIterations = _options.MiniRouting.MaxGlsIterations,
            TimeLimitMilliseconds = _cspOptions.TimeLimitMilliseconds,
            FinalCandidatesToValidate = _cspOptions.FinalCandidatesToValidate,
        };
        var polished = new GuidedLocalSearch(search.Context, polishOptions, new MiniRoutingStatistics(), clock, cancellationToken)
            .Run(bestCsp);
        foreach (var state in polished)
        {
            // Tìm kiếm cục bộ chèn được mọi điểm tùy chọn: chỉ giữ lời giải nằm trong giới hạn của CSP.
            if (search.WithinLimits(state))
            {
                search.Elite.Offer(state);
            }
        }
    }

    /// <summary>
    /// Đưa các lời giải tốt nhất của một lần duyệt (theo product rule trên mô hình) qua evaluator. Không có điểm nghỉ
    /// thì thứ tự trên mô hình trùng thứ tự trên lịch thật, nên dừng ở lịch hợp lệ đầu tiên.
    /// </summary>
    private static List<EvaluatedItinerarySchedule> EvaluateElite(
        SearchRun search,
        bool stopAtFirstValid,
        Func<RoutingContext, IReadOnlyList<int>, CspStatistics, EvaluatedItinerarySchedule?> evaluate)
    {
        var schedules = new List<EvaluatedItinerarySchedule>();
        foreach (var state in search.Elite.Ordered)
        {
            if (evaluate(search.Context, state.Sequence, search.Statistics) is not { } schedule)
            {
                continue;
            }

            schedules.Add(schedule);
            if (stopAtFirstValid)
            {
                break;
            }
        }

        return schedules;
    }

    /// <summary>Giới hạn dùng chung cho mọi lần duyệt của một lần giải: số nút và số lần evaluator trong lúc duyệt.</summary>
    private sealed class SearchBudget(int maxNodes)
    {
        public int MaxNodes { get; } = maxNodes;

        public int Used { get; set; }

        public int InSearchEvaluations { get; set; }
    }

    /// <summary>
    /// Lời giải tốt nhất đã biết cho branch and bound: vector bao gồm và phút di chuyển theo ma trận. Phút di chuyển lấy
    /// từ lịch đã qua evaluator (gồm cả chặng tới điểm nghỉ) hoặc, khi không có nghỉ, từ mô hình (hai giá trị bằng nhau).
    /// </summary>
    private sealed class Incumbent(ulong[] inclusion, int travel)
    {
        public ulong[] Inclusion { get; set; } = inclusion;

        public int Travel { get; set; } = travel;

        /// <summary>Âm khi (inclusion, travel) tốt hơn incumbent theo product rule.</summary>
        public int CompareTo(ulong[] inclusion, int travel)
        {
            var result = CspProductRule.CompareInclusion(inclusion, Inclusion);
            return result != 0 ? result : travel.CompareTo(Travel);
        }
    }

    /// <summary>K lời giải tốt nhất theo product rule trên mô hình, không trùng nhau.</summary>
    private sealed class EliteSet(CspProductRule rule, int capacity)
    {
        private readonly List<(RouteState State, ulong[] Inclusion)> _items = [];

        public RouteState? Best => _items.Count == 0 ? null : _items[0].State;

        public IEnumerable<RouteState> Ordered => _items.Select(item => item.State);

        /// <summary>
        /// Kiểm tra rẻ trước khi dựng lời giải: false khi tập đã đầy và lời giải chắc chắn kém phần tử cuối theo vector
        /// bao gồm, phút di chuyển rồi thời lượng. Hòa ở cả ba thì để <see cref="Offer(RouteState, ulong[])"/> so tiếp Id.
        /// </summary>
        public bool MayAccept(ulong[] inclusion, int travel, int finish)
        {
            if (_items.Count < capacity)
            {
                return true;
            }

            var (worst, worstInclusion) = _items[^1];
            var comparison = CspProductRule.CompareInclusion(inclusion, worstInclusion);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            comparison = travel.CompareTo(worst.TravelMinutes);
            return comparison != 0 ? comparison < 0 : finish <= worst.Finish;
        }

        public void Offer(RouteState state) => Offer(state, rule.Inclusion(state.Sequence));

        public void Offer(RouteState state, ulong[] inclusion)
        {
            var index = 0;
            while (index < _items.Count)
            {
                var comparison = rule.Compare(state, inclusion, _items[index].State, _items[index].Inclusion);
                if (comparison == 0)
                {
                    // Product rule so tới cả dãy Id, nên bằng nhau nghĩa là cùng một lộ trình.
                    return;
                }

                if (comparison < 0)
                {
                    break;
                }

                index++;
            }

            if (index >= capacity)
            {
                return;
            }

            _items.Insert(index, (state, inclusion));
            if (_items.Count > capacity)
            {
                _items.RemoveAt(_items.Count - 1);
            }
        }
    }

    /// <summary>
    /// Trạng thái của một lần duyệt cây tìm kiếm. Lộ trình đang xét nằm trong một bộ mảng dùng chung: thêm điểm khi đi
    /// xuống (<see cref="Push"/>), gỡ khi quay lui (<see cref="Pop"/>), và mỗi độ sâu có sẵn bộ đệm cho miền giá trị và
    /// thứ tự thử, nên phép duyệt không cấp phát bộ nhớ ở mỗi nút. Chỉ lời giải có thể vào elite set mới được dựng thành
    /// <see cref="RouteState"/>.
    /// </summary>
    private sealed class SearchRun
    {
        private const int UrgentSlackMinutes = 60;

        private readonly RoutingContext _ctx;
        private readonly CspProductRule _rule;
        private readonly CspOptions _options;
        private readonly Stopwatch _clock;
        private readonly SearchBudget _budget;
        private readonly bool _restApplies;
        private readonly Func<RoutingContext, IReadOnlyList<int>, CspStatistics, EvaluatedItinerarySchedule?> _evaluate;
        private readonly CancellationToken _cancellationToken;
        private readonly int _maxStops;
        private readonly int _mandatoryTotal;
        private readonly int[] _initialDomain;
        private readonly bool[] _inInitialDomain;
        private readonly int[] _minIncomingTravel;
        private readonly int[] _minIncomingRaw;
        private readonly int[] _latestPossibleStart;
        private readonly int[] _minExit;
        private readonly int[] _returnRaw;
        private readonly int _minReturnTravel;
        private readonly int _mandatoryWeight;
        private readonly Dictionary<int, int> _wipeouts = new();

        // Lộ trình đang xét. Theo vị trí i: _sequence[i], _departure[i] (giờ rời điểm). Theo độ sâu d (số điểm đã xếp):
        // _travel[d] (phút đi thô, chưa tính chặng về đích), _cost[d]. Bộ đệm miền giá trị và thứ tự thử theo độ sâu.
        private readonly int[] _sequence;
        private readonly int[] _departure;
        private readonly int[] _travel;
        private readonly decimal[] _cost;
        private readonly bool[] _contains;
        private readonly ulong[] _inclusion;
        private readonly ulong[] _optimistic;
        private readonly int[][] _domains;
        private readonly RankedValue[][] _ranked;
        private int _depth;
        private int _mandatoryPlaced;
        private bool _aborted;

        public SearchRun(
            RoutingContext ctx,
            CspProductRule rule,
            CspOptions options,
            Stopwatch clock,
            SearchBudget budget,
            bool restApplies,
            Func<RoutingContext, IReadOnlyList<int>, CspStatistics, EvaluatedItinerarySchedule?> evaluate,
            CancellationToken cancellationToken)
        {
            _ctx = ctx;
            _rule = rule;
            _options = options;
            _clock = clock;
            _budget = budget;
            _restApplies = restApplies;
            _evaluate = evaluate;
            _cancellationToken = cancellationToken;
            _maxStops = options.MaxStops ?? int.MaxValue;
            _mandatoryTotal = ctx.MandatoryNodes.Count;
            Elite = new EliteSet(rule, Math.Max(1, options.FinalCandidatesToValidate));

            // Miền giá trị ban đầu: mọi điểm bắt buộc + N điểm tùy chọn theo thứ hạng chuẩn. Giữ thứ tự hạng: forward
            // checking giữ nguyên thứ tự, nên mọi miền con cũng xếp theo hạng, đúng như cận lạc quan cần.
            _initialDomain = ctx.MandatoryNodes
                .Concat(ctx.OptionalNodes
                    .OrderBy(node => rule.RankOf(node) < 0 ? int.MaxValue : rule.RankOf(node))
                    .ThenBy(node => ctx.Nodes[node].Candidate.Id)
                    .Take(options.MaxOptionalDomainSize ?? int.MaxValue))
                .ToArray();

            var nodeCount = ctx.Nodes.Count;
            _inInitialDomain = new bool[nodeCount];
            foreach (var v in _initialDomain)
            {
                _inInitialDomain[v] = true;
            }

            // Độ sâu tối đa là số điểm trong miền.
            var maxDepth = _initialDomain.Length + 1;
            _sequence = new int[maxDepth];
            _departure = new int[maxDepth];
            _travel = new int[maxDepth + 1];
            _cost = new decimal[maxDepth + 1];
            _contains = new bool[nodeCount];
            _inclusion = new ulong[rule.Words];
            _optimistic = new ulong[rule.Words];
            _domains = new int[maxDepth][];
            _ranked = new RankedValue[maxDepth][];
            for (var depth = 0; depth < maxDepth; depth++)
            {
                _domains[depth] = new int[_initialDomain.Length];
                _ranked[depth] = new RankedValue[_initialDomain.Length];
            }

            _minIncomingTravel = new int[nodeCount];
            _minIncomingRaw = new int[nodeCount];
            _latestPossibleStart = new int[nodeCount];
            _minExit = new int[nodeCount];
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
                _minExit[v] = minExit;
                _latestPossibleStart[v] = ctx.LatestStart(v, ctx.Horizon - minExit - ctx.Nodes[v].VisitMinutes);
            }

            var maxPenalty = ctx.OptionalNodes.Select(i => ctx.Nodes[i].SkipPenalty).DefaultIfEmpty(1).Max();
            _mandatoryWeight = 2 * Math.Max(1, maxPenalty);
        }

        public CspStatistics Statistics { get; } = new();

        public RoutingContext Context => _ctx;

        public EliteSet Elite { get; }

        /// <summary>Incumbent khi lần duyệt kết thúc (đã siết bởi các lời giải tìm được); null nếu chưa có.</summary>
        public Incumbent? Incumbent { get; private set; }

        /// <summary>Lịch đã qua evaluator trong lúc duyệt và đã siết incumbent (chỉ khi có điểm nghỉ).</summary>
        public List<EvaluatedItinerarySchedule> EvaluatedSolutions { get; } = [];

        /// <summary>Giờ rời điểm cuối của lộ trình đang xét (0 ở gốc).</summary>
        private int Departure => _depth == 0 ? 0 : _departure[_depth - 1];

        /// <summary>Chỉ số ma trận của điểm cuối (điểm xuất phát ở gốc).</summary>
        private int CurrentMatrix => _depth == 0 ? _ctx.StartMatrixIndex : _ctx.Nodes[_sequence[_depth - 1]].MatrixIndex;

        /// <summary>
        /// Nhận lịch heuristic làm incumbent ban đầu nếu nó nằm trong giới hạn CSP: một cận ngoài giới hạn có thể tốt hơn
        /// mọi lời giải hợp lệ, cắt mất chúng và trở thành kết quả trả về.
        /// </summary>
        public Incumbent? AcceptIncumbent(EvaluatedItinerarySchedule? schedule)
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

            if (schedule.VisitPoiIds.Count > _maxStops
                || schedule.VisitPoiIds.Any(id => !nodeOf.TryGetValue(id, out var node) || !_inInitialDomain[node]))
            {
                return null;
            }

            Statistics.InitialIncumbentAccepted = true;
            return new Incumbent(_rule.Inclusion(schedule.VisitPoiIds), schedule.TotalMatrixTravelMinutes);
        }

        public void Run(Incumbent? incumbent)
        {
            Incumbent = incumbent;

            // Forward checking tại gốc: điểm nào không bao giờ xếp được thì loại ngay.
            var domain = _domains[0];
            var count = 0;
            foreach (var v in _initialDomain)
            {
                if (_latestPossibleStart[v] != RoutingContext.NoLatestStart
                    && (_ctx.Budget is not { } budget || _ctx.Nodes[v].Cost <= budget))
                {
                    domain[count++] = v;
                }
                else if (_ctx.Nodes[v].IsMandatory)
                {
                    RecordWipeout(v);
                    Statistics.PrunedByForwardChecking++;
                    Statistics.SearchCompleted = true;
                    return;
                }
            }

            if (!RemainingMandatoryFit())
            {
                Statistics.PrunedByForwardChecking++;
                Statistics.SearchCompleted = true;
                return;
            }

            Search(count);
            Statistics.SearchCompleted = !_aborted;
        }

        /// <summary>Lời giải không vượt MaxStops và chỉ dùng điểm trong miền giá trị ban đầu (top-N tùy chọn).</summary>
        public bool WithinLimits(RouteState state) =>
            state.Count <= _maxStops && state.Sequence.All(node => _inInitialDomain[node]);

        public IReadOnlyList<long> ConflictingMandatoryPoiIds() =>
            _wipeouts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => _ctx.Nodes[pair.Key].Candidate.Id)
                .Take(3)
                .Select(pair => _ctx.Nodes[pair.Key].Candidate.Id)
                .ToArray();

        /// <summary>Duyệt lộ trình đang xét, với <paramref name="domainCount"/> phần tử đầu của <c>_domains[_depth]</c> làm miền.</summary>
        private void Search(int domainCount)
        {
            if (!TryExpand())
            {
                return;
            }

            // Gán END tại đây là một lời giải khi đã đủ điểm bắt buộc và về thẳng đích kịp giờ. Lộ trình chưa về
            // kịp vẫn được mở rộng tiếp: điểm kế tiếp có thể nằm trên đường tắt về đích.
            var departure = Departure;
            var current = CurrentMatrix;
            if (_depth > 0
                && _mandatoryPlaced == _mandatoryTotal
                && departure + _ctx.ReturnTravel(current) <= _ctx.Horizon)
            {
                RecordSolution(departure, current);
            }

            if (_depth >= _maxStops || domainCount == 0)
            {
                return;
            }

            // Branch and bound: nhánh này không thể tốt hơn incumbent theo product rule thì cắt.
            var domain = _domains[_depth];
            if (!CanImprove(domain, domainCount, departure, current))
            {
                Statistics.PrunedByBound++;
                return;
            }

            var ranked = _ranked[_depth];
            var valueCount = OrderValues(domain, domainCount, departure, current, ranked);
            for (var i = 0; i < valueCount; i++)
            {
                if (_aborted)
                {
                    return;
                }

                // Gán S(k+1) = value và kiểm tra ràng buộc C1–C4 (C2 đã kiểm tra khi xếp thứ tự). C5 (kịp về đích) chỉ
                // kiểm tra theo cận dưới _minExit: về thẳng đích bị trễ chưa loại được nhánh, vì có thể về qua một điểm
                // khác nhanh hơn.
                var value = ranked[i].Value;
                var start = ranked[i].Start;
                var node = _ctx.Nodes[value];
                if ((_ctx.Budget is { } budget && _cost[_depth] + node.Cost > budget)
                    || start + node.VisitMinutes + _minExit[value] > _ctx.Horizon)
                {
                    Statistics.PrunedByConstraints++;
                    continue;
                }

                Push(value, start, current);

                // Forward checking: thu hẹp miền vào bộ đệm của độ sâu mới; mất điểm bắt buộc thì quay lui ngay.
                var childCount = ForwardCheck(domain, domainCount, value, out var wipedMandatory);
                if (childCount < 0)
                {
                    RecordWipeout(wipedMandatory);
                    Statistics.PrunedByForwardChecking++;
                }
                else if (!RemainingMandatoryFit())
                {
                    Statistics.PrunedByForwardChecking++;
                }
                else
                {
                    Search(childCount);
                }

                Pop();
            }
        }

        private void Push(int value, int start, int fromMatrix)
        {
            var node = _ctx.Nodes[value];
            _sequence[_depth] = value;
            _departure[_depth] = start + node.VisitMinutes;
            _travel[_depth + 1] = _travel[_depth] + _ctx.RawTravel(fromMatrix, node.MatrixIndex);
            _cost[_depth + 1] = _cost[_depth] + node.Cost;
            _contains[value] = true;
            CspProductRule.Set(_inclusion, _rule.RankOf(value));
            if (node.IsMandatory)
            {
                _mandatoryPlaced++;
            }

            _depth++;
        }

        private void Pop()
        {
            _depth--;
            var value = _sequence[_depth];
            _contains[value] = false;
            CspProductRule.Clear(_inclusion, _rule.RankOf(value));
            if (_ctx.Nodes[value].IsMandatory)
            {
                _mandatoryPlaced--;
            }
        }

        /// <summary>
        /// Ghi nhận lộ trình đang xét là một lời giải. Phút di chuyển và mốc về đích tính trong O(1); lộ trình chỉ được dựng
        /// thành <see cref="RouteState"/> khi có thể vào elite set.
        /// </summary>
        private void RecordSolution(int departure, int current)
        {
            Statistics.SolutionsFound++;
            Statistics.NodesToFirstSolution ??= Statistics.NodesExpanded;
            var travel = _travel[_depth] + _ctx.RawTravel(current, _ctx.EndMatrixIndex);
            var finish = departure + _ctx.ReturnTravel(current);
            if (Elite.MayAccept(_inclusion, travel, finish)
                && RouteState.Build(_ctx, new ArraySegment<int>(_sequence, 0, _depth)) is { } solution)
            {
                Elite.Offer(solution, (ulong[])_inclusion.Clone());
            }

            Tighten(travel);
        }

        /// <summary>
        /// Siết incumbent bằng lộ trình đang xét nếu nó tốt hơn theo product rule. Không có điểm nghỉ thì mô hình trùng lịch
        /// thật, nên dùng thẳng lời giải. Có điểm nghỉ thì lời giải phải qua evaluator trước: một lời giải mô hình chưa kiểm
        /// tra có thể hỏng hoặc tệ hơn sau khi chèn nghỉ, và siết cận bằng nó sẽ cắt mất những nhánh có lịch thật tốt hơn.
        /// </summary>
        private void Tighten(int travel)
        {
            if (Incumbent is not null && Incumbent.CompareTo(_inclusion, travel) >= 0)
            {
                return;
            }

            if (!_restApplies)
            {
                Replace((ulong[])_inclusion.Clone(), travel);
                return;
            }

            if (_budget.InSearchEvaluations >= MaxInSearchEvaluations)
            {
                return;
            }

            _budget.InSearchEvaluations++;
            if (_evaluate(_ctx, _sequence[.._depth], Statistics) is not { } schedule)
            {
                return;
            }

            EvaluatedSolutions.Add(schedule);
            if (Incumbent is null || Incumbent.CompareTo(_inclusion, schedule.TotalMatrixTravelMinutes) < 0)
            {
                Replace((ulong[])_inclusion.Clone(), schedule.TotalMatrixTravelMinutes);
            }
        }

        private void Replace(ulong[] inclusion, int travel)
        {
            if (Incumbent is null)
            {
                Incumbent = new Incumbent(inclusion, travel);
                return;
            }

            Incumbent.Inclusion = inclusion;
            Incumbent.Travel = travel;
        }

        /// <summary>
        /// Cận lạc quan theo product rule (admissible). Mọi lời giải mở rộng từ lộ trình đang xét chỉ có thể thêm điểm tùy
        /// chọn còn trong miền, trong giới hạn thời gian còn lại (mỗi điểm tốn ít nhất đường đến ngắn nhất + thời gian tham
        /// quan), ngân sách còn lại và số chỗ còn trống, sau khi đã dành chỗ cho điểm bắt buộc còn thiếu. Với thứ tự từ
        /// điển, chọn tham lam theo hạng (thêm nếu còn vừa) cho đúng vector bao gồm lớn nhất của bài nới lỏng đó. Vector này
        /// kém incumbent thì cắt. Bằng incumbent thì lời giải phải thêm đúng các điểm đã chọn, và cắt khi cận dưới phút di
        /// chuyển (đã đi + đường đến ngắn nhất của mọi điểm phải thêm + chặng về ngắn nhất) vượt incumbent.
        /// </summary>
        private bool CanImprove(int[] domain, int domainCount, int departure, int current)
        {
            if (Incumbent is null)
            {
                return true;
            }

            long travel = _travel[_depth];
            var timeLeft = _ctx.Horizon - departure - _minReturnTravel;
            var budgetLeft = _ctx.Budget - _cost[_depth];
            var remainingMandatory = 0;
            foreach (var m in _ctx.MandatoryNodes)
            {
                if (!_contains[m])
                {
                    remainingMandatory++;
                    travel += _minIncomingRaw[m];
                    timeLeft -= _minIncomingTravel[m] + _ctx.Nodes[m].VisitMinutes;
                    budgetLeft -= _ctx.Nodes[m].Cost;
                }
            }

            var slots = _maxStops - _depth - remainingMandatory;
            var optimistic = _optimistic;
            Array.Copy(_inclusion, optimistic, optimistic.Length);
            var finalReturn = _ctx.RawTravel(current, _ctx.EndMatrixIndex);
            for (var i = 0; i < domainCount; i++)
            {
                var v = domain[i];
                finalReturn = Math.Min(finalReturn, _returnRaw[v]);
                var node = _ctx.Nodes[v];
                if (node.IsMandatory || slots <= 0)
                {
                    continue;
                }

                var footprint = _minIncomingTravel[v] + node.VisitMinutes;
                if (footprint <= timeLeft && (budgetLeft is not { } left || node.Cost <= left))
                {
                    CspProductRule.Set(optimistic, _rule.RankOf(v));
                    timeLeft -= footprint;
                    budgetLeft -= node.Cost;
                    slots--;
                    travel += _minIncomingRaw[v];
                }
            }

            var comparison = CspProductRule.CompareInclusion(optimistic, Incumbent.Inclusion);
            if (comparison != 0)
            {
                return comparison < 0;
            }

            // Bằng phút di chuyển vẫn có thể thắng ở thời lượng hoặc dãy Id, nên chỉ cắt khi cận vượt hẳn incumbent.
            return travel + finalReturn <= Incumbent.Travel;
        }

        /// <summary>
        /// Giữ lại trong miền những POI còn có thể xếp ở một vị trí nào đó phía sau (vừa gán <paramref name="placed"/>). Một
        /// POI bị loại khi ngay cả giờ đến sớm nhất có thể (đi thẳng tới, hoặc từ điểm gần nó nhất) cũng đã muộn hơn giờ
        /// bắt đầu muộn nhất của nó, hoặc khi phần ngân sách còn lại không đủ. Cách loại này không bao giờ bỏ sót lời
        /// giải. Ghi miền mới vào bộ đệm của độ sâu hiện tại và trả về số phần tử; trả về -1 khi một điểm bắt buộc bị loại.
        /// </summary>
        private int ForwardCheck(int[] domain, int domainCount, int placed, out int wipedMandatory)
        {
            wipedMandatory = -1;
            var departure = Departure;
            var current = CurrentMatrix;
            var remainingBudget = _ctx.Budget - _cost[_depth];
            var result = _domains[_depth];
            var count = 0;

            for (var i = 0; i < domainCount; i++)
            {
                var v = domain[i];
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
                    result[count++] = v;
                }
                else if (node.IsMandatory)
                {
                    wipedMandatory = v;
                    return -1;
                }
            }

            return count;
        }

        /// <summary>
        /// Kiểm tra nới lỏng: các điểm bắt buộc chưa xếp, mỗi điểm tốn ít nhất (đường đến ngắn nhất + thời gian
        /// tham quan), cộng chặng về đích ngắn nhất, phải còn vừa thời gian; tổng chi phí phải vừa ngân sách.
        /// </summary>
        private bool RemainingMandatoryFit()
        {
            var time = Departure;
            var cost = _cost[_depth];
            var remaining = 0;

            foreach (var m in _ctx.MandatoryNodes)
            {
                if (_contains[m])
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
        /// Thứ tự thử giá trị: điểm bắt buộc sắp hết giờ (độ trễ cho phép dưới 60 phút) trước, theo độ trễ
        /// tăng dần (kiểu MRV); sau đó theo "phút đi thêm trừ lợi ích" tăng dần (kiểu LCV), lợi ích của điểm
        /// bắt buộc lớn hơn mọi điểm tùy chọn. Giá trị không thể bắt đầu đúng giờ bị loại ngay. Ghi vào
        /// <paramref name="ranked"/> (bộ đệm của độ sâu hiện tại) và trả về số giá trị.
        /// </summary>
        private int OrderValues(int[] domain, int domainCount, int departure, int current, RankedValue[] ranked)
        {
            var count = 0;
            for (var i = 0; i < domainCount; i++)
            {
                var v = domain[i];
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
                ranked[count++] = new RankedValue(v, start, node.IsMandatory && slack < UrgentSlackMinutes, slack, score, node.Candidate.Id);
            }

            // Id của POI không trùng nên thứ tự là toàn phần: sắp xếp không ổn định vẫn cho kết quả tất định.
            Array.Sort(ranked, 0, count, RankedValueComparer.Instance);
            return count;
        }

        private readonly record struct RankedValue(int Value, int Start, bool Urgent, int Slack, int Score, long Id);

        /// <summary>Khẩn cấp trước; trong nhóm khẩn cấp theo độ trễ tăng dần; rồi theo điểm, rồi theo Id.</summary>
        private sealed class RankedValueComparer : IComparer<RankedValue>
        {
            public static readonly RankedValueComparer Instance = new();

            public int Compare(RankedValue x, RankedValue y)
            {
                var result = y.Urgent.CompareTo(x.Urgent);
                if (result == 0)
                {
                    result = (x.Urgent ? x.Slack : 0).CompareTo(y.Urgent ? y.Slack : 0);
                }

                if (result == 0)
                {
                    result = x.Score.CompareTo(y.Score);
                }

                return result != 0 ? result : x.Id.CompareTo(y.Id);
            }
        }

        private void RecordWipeout(int mandatoryNode) =>
            _wipeouts[mandatoryNode] = _wipeouts.GetValueOrDefault(mandatoryNode) + 1;

        /// <summary>
        /// Xin một nút từ budget chung trước khi duyệt. Budget được kiểm tra trước khi đếm, nên nút bị từ chối không
        /// được tính và <see cref="CspStatistics.NodesExpanded"/> không bao giờ vượt <c>MaxNodes</c>, kể cả khi hai lần
        /// chạy (có và không dự phòng nghỉ) dùng chung budget. Trả về false khi phải dừng (hết budget, hết giờ).
        /// </summary>
        private bool TryExpand()
        {
            if (_aborted)
            {
                return false;
            }

            if (_budget.Used >= _budget.MaxNodes)
            {
                Statistics.NodeLimitReached = true;
                _aborted = true;
                return false;
            }

            if (((_budget.Used + 1) & 255) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (_clock.ElapsedMilliseconds >= _options.TimeLimitMilliseconds)
                {
                    Statistics.TimeLimitReached = true;
                    _aborted = true;
                    return false;
                }
            }

            _budget.Used++;
            Statistics.NodesExpanded++;
            return true;
        }
    }
}
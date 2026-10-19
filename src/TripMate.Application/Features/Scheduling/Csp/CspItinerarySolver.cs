using System.Diagnostics;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Constants;
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

    /// <summary>
    /// True khi không lời giải nào của CSP tốt hơn incumbent ban đầu, nên chính lịch heuristic được trả về. Lời giải CSP
    /// trùng hệt lịch heuristic cũng tính, vì bước chọn cuối giữ lịch đứng trước (lịch heuristic) khi hai lịch bằng nhau.
    /// </summary>
    public bool ReturnedInitialIncumbent { get; internal set; }

    /// <summary>Số lời giải đã đưa qua <see cref="ItineraryScheduleEvaluator"/> (trong lúc duyệt và ở bước chọn cuối).</summary>
    public int EvaluatorCalls { get; internal set; }

    /// <summary>
    /// True khi lượt evaluator trong lúc duyệt (khi có điểm nghỉ) đã hết: từ đó incumbent chỉ còn được siết bởi lịch đã kiểm
    /// tra, nên kết quả vẫn đúng nhưng cắt nhánh kém hơn và lịch tối ưu sau khi chèn nghỉ có thể bị bỏ lỡ.
    /// </summary>
    public bool InSearchEvaluationLimitReached { get; internal set; }

    /// <summary>
    /// True khi lượt evaluator dành cho lời giải hòa incumbent (khi có điểm nghỉ) đã hết: lịch vẫn tốt nhất ở vector bao gồm
    /// và phút di chuyển, nhưng có thể chưa tốt nhất ở thời lượng, giờ kết thúc hoặc dãy Id.
    /// </summary>
    public bool TieEvaluationLimitReached { get; internal set; }

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
        InSearchEvaluationLimitReached |= earlier.InSearchEvaluationLimitReached;
        TieEvaluationLimitReached |= earlier.TieEvaluationLimitReached;
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

    internal IReadOnlyList<IReadOnlyList<long>> EliteVisitIds { get; init; } = [];
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
    /// incumbent chỉ còn được siết bởi lịch đã kiểm tra trước đó (<see cref="CspStatistics.InSearchEvaluationLimitReached"/>).
    /// Với Frequent, giờ nghỉ đẩy lùi giờ đến nên nhiều lời giải mô hình hỏng sau khi chèn nghỉ: trên 300 scenario
    /// Frequent của corpus benchmark, 256 lượt hết ở 279 scenario (CSP tốt hơn heuristic ở 32), 4.096 lượt hết ở 104 (67),
    /// 16.384 lượt chỉ hết ở 12 (80), với thêm khoảng 11 ms mỗi lần giải.
    /// </summary>
    private const int MaxInSearchEvaluations = 16_384;

    /// <summary>
    /// Số lần đưa qua evaluator những lời giải hòa incumbent ở vector bao gồm và phút di chuyển (chỉ khi có điểm nghỉ). Mô
    /// hình chưa tính nghỉ, nên lịch thật của chúng vẫn có thể thắng ở thời lượng, giờ kết thúc hoặc dãy Id. Lượt này tách
    /// khỏi <see cref="MaxInSearchEvaluations"/>, để lời giải hòa không lấy lượt của lời giải tốt hơn hẳn
    /// (<see cref="CspStatistics.TieEvaluationLimitReached"/>).
    /// </summary>
    private const int MaxTieEvaluations = 4_096;

    private readonly ItineraryScheduleEvaluator _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly SchedulingGenerationOptions _options = options ?? new SchedulingGenerationOptions();
    private readonly CspOptions _cspOptions = cspOptions ?? options?.Csp ?? new CspOptions();

    /// <summary>
    /// Điểm chèn cho test: chạy ngay trước mỗi lần gọi evaluator, nhận đúng token mà evaluator nhận. Dùng để mô phỏng
    /// evaluator chậm, có hoặc không tôn trọng token. Production không đặt.
    /// </summary>
    internal Action<CancellationToken>? BeforeEvaluation { get; init; }

    internal static RoutingContext CreatePrimaryContext(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        SchedulingGenerationOptions options) =>
        RoutingContext.Create(input, matrix, matrixCandidates, candidateMatrixIndices, options, options.MiniRouting);

    public CspResult Solve(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        CancellationToken cancellationToken = default) =>
        Solve(input, matrix, matrixCandidates, candidateMatrixIndices, initialIncumbent: null, weighted: null, cancellationToken);

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
        CancellationToken cancellationToken = default) =>
        Solve(input, matrix, matrixCandidates, candidateMatrixIndices, initialIncumbent, weighted: null, cancellationToken);

    public CspResult Solve(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        EvaluatedItinerarySchedule? initialIncumbent,
        WeightedRequestInputs? weighted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(matrixCandidates);
        ArgumentNullException.ThrowIfNull(candidateMatrixIndices);

        if (_cspOptions.Mode == ObjectiveMode.WeightedUtility && _cspOptions.PolishWithLocalSearch)
        {
            throw new NotSupportedException("PolishWithLocalSearch (GLS) is not supported in WeightedUtility mode.");
        }

        var clock = Stopwatch.StartNew();

        // Một mốc thời gian cứng cho cả lần giải (review Phase 3): bước duyệt, mọi lần gọi evaluator, polish và bước
        // chấm cuối đều xem cùng một đồng hồ. Chạm mốc ở bất kỳ đâu thì kết quả CSP bị bỏ (R5).
        using var deadline = new SolveDeadline(clock, _cspOptions.TimeLimitMilliseconds, cancellationToken);
        var budget = new SearchBudget(_cspOptions.MaxNodes);
        var ctx = CreatePrimaryContext(input, matrix, matrixCandidates, candidateMatrixIndices, _options);

        WeightedObjectiveInputs? weightedInputs = null;
        CspProductRule rule;
        if (_cspOptions.Mode == ObjectiveMode.WeightedUtility)
        {
            if (weighted is null)
            {
                throw new InvalidOperationException("WeightedRequestInputs must be provided when ObjectiveMode is WeightedUtility.");
            }
            weightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(ctx, weighted);
            rule = CspProductRule.BuildRankRuleFromBase(ctx, weightedInputs.Base);
        }
        else
        {
            rule = CspProductRule.Create(ctx, matrixCandidates, candidateMatrixIndices, matrix);
        }


        // Khi có điểm nghỉ, lịch thật khác mô hình: chỉ lời giải đã qua evaluator mới được siết incumbent.
        var restApplies = input.RestPreference == RestPreference.Frequent
            || (input.RestPreference == RestPreference.Auto && input.AvailableMinutes >= 300);
        var evaluate = (RoutingContext context, IReadOnlyList<int> nodes, CspStatistics statistics) =>
        {
            deadline.ThrowIfExpired();
            statistics.EvaluatorCalls++;
            var sequence = nodes.Select(node => context.Nodes[node].Candidate).ToArray();
            EvaluatedItinerarySchedule? schedule;
            try
            {
                BeforeEvaluation?.Invoke(deadline.Token);
                schedule = _evaluator.Evaluate(input, sequence, matrix, matrixCandidates, candidateMatrixIndices, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Token của mốc bị hủy giữa một lần chấm: đó là chạm mốc, không phải bên gọi hủy.
                throw new TimeLimitExceededException();
            }

            deadline.ThrowIfExpired();
            return schedule;
        };

        var search = new SearchRun(ctx, rule, _cspOptions, deadline, budget, restApplies, evaluate, cancellationToken, weightedInputs);
        if (ctx.MandatoryNodes.Count != input.MandatoryPoiIds.Count)
        {
            search.Statistics.Elapsed = clock.Elapsed;
            return new CspResult(null, search.Statistics, []);
        }

        var incumbent = search.AcceptIncumbent(initialIncumbent);
        var scheduleComparer = search.ScheduleComparer;
        var candidates = new List<(EvaluatedItinerarySchedule Schedule, bool Relaxed)>();
        if (incumbent is not null && search.AcceptedSchedule is not null)
        {
            candidates.Add((search.AcceptedSchedule, false));
        }

        SearchRun? relaxed = null;
        try
        {
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
                // Mô hình nới lỏng thật sự: không dự phòng thời gian nghỉ, và mỗi chặng được đi vòng qua một điểm nghỉ nếu
                // đường đó ngắn hơn (R9), vì lịch thật có thể tới nơi sớm hơn và đi ít hơn mô hình đi thẳng.
                var relaxedCtx = RoutingContext.Create(
                    input, matrix, matrixCandidates, candidateMatrixIndices, _options, _options.MiniRouting,
                    reserveRestTime: false, restShortcuts: true);

                WeightedObjectiveInputs? relaxedWeightedInputs = null;
                CspProductRule relaxedRule = rule;
                if (_cspOptions.Mode == ObjectiveMode.WeightedUtility)
                {
                    relaxedWeightedInputs = ObjectiveKernelFactory.BuildWeightedInputs(relaxedCtx, weighted!);
                    relaxedRule = CspProductRule.BuildRankRuleFromBase(relaxedCtx, relaxedWeightedInputs.Base);
                }

                relaxed = new SearchRun(relaxedCtx, relaxedRule, _cspOptions, deadline, budget, restApplies, evaluate, cancellationToken, relaxedWeightedInputs);

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
            }

            // Kiểm tra cuối: bắt cả phần việc không tự xem đồng hồ (dựng mô hình, giữa các bước).
            deadline.ThrowIfExpired();
        }
        catch (TimeLimitExceededException)
        {
            var timedOut = relaxed ?? search;
            timedOut.Statistics.TimeLimitReached = true;
            if (relaxed is not null)
            {
                relaxed.Statistics.AddEarlierRun(search.Statistics);
            }

            return DiscardTimedOutResult(timedOut, clock);
        }

        if (relaxed is not null)
        {
            relaxed.Statistics.AddEarlierRun(search.Statistics);
            search = relaxed;
        }

        search.Statistics.Elapsed = clock.Elapsed;
        var eliteVisitIds = search.Elite.Ordered
            .Select(s => (IReadOnlyList<long>)s.Sequence.Select(n => search.Context.Nodes[n].Candidate.Id).ToArray())
            .ToArray();

        if (candidates.Count == 0)
        {
            return new CspResult(null, search.Statistics, search.ConflictingMandatoryPoiIds())
            {
                EliteVisitIds = eliteVisitIds,
            };
        }

        // Chọn lịch bằng đúng quy tắc của hàm mục tiêu trên kết quả của evaluator; bằng nhau thì giữ lịch đứng trước.
        // Dùng một comparer duy nhất lấy từ kernel của primary run cho cả tập ứng viên.
        var best = candidates[0];
        foreach (var candidate in candidates.Skip(1))
        {
            if (scheduleComparer.Compare(candidate.Schedule, best.Schedule) < 0)
            {
                best = candidate;
            }
        }

        search.Statistics.ChoseRelaxedRestModel = best.Relaxed;
        search.Statistics.ReturnedInitialIncumbent = ReferenceEquals(best.Schedule, initialIncumbent);
        return new CspResult(best.Schedule, search.Statistics, [])
        {
            EliteVisitIds = eliteVisitIds,
        };
    }

    /// <summary>
    /// R5: lưới an toàn thời gian bị chạm thì kết quả CSP dở dang phụ thuộc tốc độ máy, nên bị bỏ hẳn, kể cả lịch heuristic
    /// đang là incumbent. Bên gọi trả lịch heuristic nguyên vẹn (<see cref="CspStatistics.TimeLimitReached"/>), nên lịch trả
    /// về không bao giờ là kết quả dở dang. Mốc có bị chạm hay không vẫn phụ thuộc máy (xem
    /// <see cref="CspOptions.TimeLimitMilliseconds"/>).
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
        var polishStatistics = new MiniRoutingStatistics();
        var polished = new GuidedLocalSearch(search.Context, polishOptions, polishStatistics, clock, cancellationToken)
            .Run(bestCsp);

        // Tìm kiếm cục bộ dừng ở cùng mốc và cùng đồng hồ: dừng vì mốc thì lời giải đã polish dở dang, phụ thuộc tốc độ
        // máy, nên cũng bị bỏ như ở bước duyệt.
        if (polishStatistics.TimeLimitReached)
        {
            throw new TimeLimitExceededException();
        }

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

    /// <summary>

    /// Mốc thời gian chung của một lần giải (<see cref="CspOptions.TimeLimitMilliseconds"/>), đo bằng đồng hồ bắt đầu cùng
    /// lần giải. <see cref="Token"/> bị hủy khi tới mốc hoặc khi bên gọi hủy, để một lần chấm đang chạy dừng ngay.
    /// </summary>
    internal sealed class SolveDeadline : IDisposable
    {
        private readonly Stopwatch _clock;
        private readonly int _limitMilliseconds;
        private readonly CancellationTokenSource _source;

        public SolveDeadline(Stopwatch clock, int limitMilliseconds, CancellationToken callerToken)
        {
            _clock = clock;
            _limitMilliseconds = limitMilliseconds;
            _source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            _source.CancelAfter(Math.Max(0, limitMilliseconds));
        }

        public CancellationToken Token => _source.Token;

        public bool Expired => _clock.ElapsedMilliseconds >= _limitMilliseconds;

        public void ThrowIfExpired()
        {
            if (Expired)
            {
                throw new TimeLimitExceededException();
            }
        }

        public void Dispose() => _source.Dispose();
    }

    /// <summary>Chạm mốc thời gian ngoài bước duyệt (evaluator, polish, kiểm tra cuối); Solve bắt và bỏ kết quả CSP.</summary>
    private sealed class TimeLimitExceededException : Exception;

    /// <summary>Giới hạn dùng chung cho mọi lần duyệt của một lần giải: số nút và số lần evaluator trong lúc duyệt.</summary>
    internal sealed class SearchBudget(int maxNodes)
    {
        public int MaxNodes { get; } = maxNodes;

        public int Used { get; set; }

        public int InSearchEvaluations { get; set; }

        public int TieEvaluations { get; set; }
    }

    /// <summary>
    /// Trạng thái của một lần duyệt cây tìm kiếm. Lộ trình đang xét nằm trong một bộ mảng dùng chung: thêm điểm khi đi
    /// xuống (<see cref="Push"/>), gỡ khi quay lui (<see cref="Pop"/>), và mỗi độ sâu có sẵn bộ đệm cho miền giá trị và
    /// thứ tự thử, nên phép duyệt không cấp phát bộ nhớ ở mỗi nút. Chỉ lời giải có thể vào elite set mới được dựng thành
    /// <see cref="RouteState"/>.
    /// </summary>
    internal sealed class SearchRun
    {
        private const int UrgentSlackMinutes = 60;

        private readonly RoutingContext _ctx;
        private readonly CspProductRule _rule;
        private readonly CspOptions _options;
        private readonly SolveDeadline _deadline;
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
        private readonly SearchBuffers _buffers;
        private readonly SearchPrecomputed _pre;
        private readonly IObjectiveKernel _kernel;
        internal IObjectiveKernel Kernel => _kernel;
        private readonly int[][] _domains;
        private readonly RankedValue[][] _ranked;
        private readonly WeightedObjectiveInputs? _weightedInputs;
        private int _depth;
        private int _mandatoryPlaced;
        private bool _aborted;

        public SearchRun(
            RoutingContext ctx,
            CspProductRule rule,
            CspOptions options,
            SolveDeadline deadline,
            SearchBudget budget,
            bool restApplies,
            Func<RoutingContext, IReadOnlyList<int>, CspStatistics, EvaluatedItinerarySchedule?> evaluate,
            CancellationToken cancellationToken,
            WeightedObjectiveInputs? weightedInputs = null,
            IObjectiveKernel? customKernel = null)
        {
            _ctx = ctx;
            _rule = rule;
            _options = options;
            _deadline = deadline;
            _budget = budget;
            _restApplies = restApplies;
            _evaluate = evaluate;
            _cancellationToken = cancellationToken;
            _weightedInputs = weightedInputs;
            _maxStops = options.MaxStops ?? int.MaxValue;
            _mandatoryTotal = ctx.MandatoryNodes.Count;

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
            _buffers = new SearchBuffers(maxDepth, nodeCount);
            _sequence = _buffers.Sequence;
            _departure = _buffers.Departure;
            _travel = _buffers.Travel;
            _cost = _buffers.Cost;
            _contains = _buffers.Contains;
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

            _pre = new SearchPrecomputed(
                _minIncomingTravel,
                _minIncomingRaw,
                _latestPossibleStart,
                _minExit,
                _returnRaw,
                _minReturnTravel,
                _mandatoryWeight,
                _initialDomain,
                _inInitialDomain);

            _kernel = customKernel ?? ObjectiveKernelFactory.Create(rule, options, weightedInputs);
            _kernel.Initialize(ctx, _buffers, _pre, Statistics);
            Elite = new EliteSet(_kernel.EliteOrdering, Math.Max(1, options.FinalCandidatesToValidate));
        }

        public CspStatistics Statistics { get; } = new();

        public RoutingContext Context => _ctx;

        public EliteSet Elite { get; }

        public IComparer<EvaluatedItinerarySchedule> ScheduleComparer => _kernel.ScheduleComparer;

        /// <summary>Incumbent khi lần duyệt kết thúc (đã siết bởi các lời giải tìm được); null nếu chưa có.</summary>
        public IncumbentToken? Incumbent => _kernel.ExportIncumbent();

        /// <summary>Lịch đã qua evaluator trong lúc duyệt và đã siết incumbent (chỉ khi có điểm nghỉ).</summary>
        public List<EvaluatedItinerarySchedule> EvaluatedSolutions { get; } = [];

        /// <summary>Giờ rời điểm cuối của lộ trình đang xét (0 ở gốc).</summary>
        private int Departure => _depth == 0 ? 0 : _departure[_depth - 1];

        /// <summary>Chỉ số ma trận của điểm cuối (điểm xuất phát ở gốc).</summary>
        private int CurrentMatrix => _depth == 0 ? _ctx.StartMatrixIndex : _ctx.Nodes[_sequence[_depth - 1]].MatrixIndex;

        public EvaluatedItinerarySchedule? AcceptedSchedule { get; private set; }

        /// <summary>
        /// Nhận lịch heuristic hoặc tính incumbent tham lam (nếu là WeightedUtility) làm incumbent ban đầu nếu nó nằm trong giới hạn CSP.
        /// </summary>
        public IncumbentToken? AcceptIncumbent(EvaluatedItinerarySchedule? schedule)
        {
            var bestSchedule = schedule;
            if (_options.Mode == ObjectiveMode.WeightedUtility && _weightedInputs is not null)
            {
                var greedy = GreedyWeightedIncumbent.Build(_ctx, _weightedInputs, _options, _evaluate, Statistics, _initialDomain);
                if (greedy is not null)
                {
                    if (bestSchedule is null)
                    {
                        bestSchedule = greedy;
                    }
                    else
                    {
                        var jHeuristic = ComputeModelObjective(_ctx, _weightedInputs, bestSchedule);
                        var jGreedy = ComputeModelObjective(_ctx, _weightedInputs, greedy);
                        if (jGreedy > jHeuristic)
                        {
                            bestSchedule = greedy;
                        }
                    }
                }
            }

            AcceptedSchedule = bestSchedule;
            return _kernel.AcceptInitial(bestSchedule);
        }

        private static long ComputeModelObjective(RoutingContext ctx, WeightedObjectiveInputs inputs, EvaluatedItinerarySchedule schedule)
        {
            var nodeOf = new Dictionary<long, int>(ctx.Nodes.Count);
            for (var i = 0; i < ctx.Nodes.Count; i++)
            {
                nodeOf[ctx.Nodes[i].Candidate.Id] = i;
            }

            var seq = new int[schedule.VisitPoiIds.Count];
            for (var i = 0; i < schedule.VisitPoiIds.Count; i++)
            {
                if (!nodeOf.TryGetValue(schedule.VisitPoiIds[i], out var node))
                {
                    return long.MinValue;
                }
                seq[i] = node;
            }

            if (RouteState.Build(ctx, seq) is not { } route)
            {
                return long.MinValue;
            }

            var catScratch = new int[inputs.CategoryCount];
            return ObjectiveCalculator.Replay(inputs, ctx, route.Sequence, route.Earliest, catScratch);
        }

        public void Run(IncumbentToken? incumbent)
        {
            _kernel.ImportIncumbent(incumbent);

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
            if (!_kernel.CanImprove(domain, domainCount, _depth, departure, current))
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
            _kernel.OnPush(_depth, value, start, fromMatrix);
            if (node.IsMandatory)
            {
                _mandatoryPlaced++;
            }

            _depth++;
            _buffers.Depth = _depth;
            _buffers.MandatoryPlaced = _mandatoryPlaced;
        }

        private void Pop()
        {
            _depth--;
            _buffers.Depth = _depth;
            var value = _sequence[_depth];
            _contains[value] = false;
            _kernel.OnPop(_depth, value);
            if (_ctx.Nodes[value].IsMandatory)
            {
                _mandatoryPlaced--;
            }

            _buffers.MandatoryPlaced = _mandatoryPlaced;
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
            var key = _kernel.PeekEliteKey(_depth, departure, current, travel, finish);
            if (Elite.MayAccept(key)
                && RouteState.Build(_ctx, new ArraySegment<int>(_sequence, 0, _depth)) is { } solution)
            {
                Elite.Offer(solution, _kernel.SnapshotEliteKey(_depth, departure, current, travel, finish));
            }

            Tighten(travel);
        }

        /// <summary>
        /// Siết incumbent bằng lộ trình đang xét nếu nó tốt hơn theo product rule. Không có điểm nghỉ thì mô hình trùng lịch
        /// thật, nên dùng thẳng lời giải. Có điểm nghỉ thì lời giải phải qua evaluator trước: một lời giải mô hình chưa kiểm
        /// tra có thể hỏng hoặc tệ hơn sau khi chèn nghỉ, và siết cận bằng nó sẽ cắt mất những nhánh có lịch thật tốt hơn.
        /// Lời giải hòa incumbent cũng qua evaluator (lượt riêng), vì lịch thật của nó có thể thắng ở các mức sau của product
        /// rule mà mô hình không thấy; bước chọn cuối so chúng bằng đủ product rule.
        /// </summary>
        private void Tighten(int travel)
        {
            var comparison = _kernel.CompareToIncumbent(_depth, Departure, CurrentMatrix, travel);
            if (comparison > 0 || (comparison == 0 && !_restApplies))
            {
                return;
            }

            if (!_restApplies)
            {
                _kernel.AcceptIncumbent(_depth, Departure, CurrentMatrix, travel);
                return;
            }

            if (comparison == 0)
            {
                if (_budget.TieEvaluations >= MaxTieEvaluations)
                {
                    Statistics.TieEvaluationLimitReached = true;
                    return;
                }

                _budget.TieEvaluations++;
            }
            else
            {
                if (_budget.InSearchEvaluations >= MaxInSearchEvaluations)
                {
                    Statistics.InSearchEvaluationLimitReached = true;
                    return;
                }

                _budget.InSearchEvaluations++;
            }

            if (_evaluate(_ctx, _sequence[.._depth], Statistics) is not { } schedule)
            {
                return;
            }

            EvaluatedSolutions.Add(schedule);
            if (!_kernel.HasIncumbent || _kernel.CompareEvaluated(schedule) < 0)
            {
                _kernel.AcceptEvaluated(schedule);
            }
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
                var score = _kernel.ValueScore(v, start, current, slack);
                ranked[count++] = new RankedValue(v, start, node.IsMandatory && slack < UrgentSlackMinutes, slack, score, node.Candidate.Id);
            }

            // Id của POI không trùng nên thứ tự là toàn phần: sắp xếp không ổn định vẫn cho kết quả tất định.
            Array.Sort(ranked, 0, count, RankedValueComparer.Instance);
            return count;
        }

        private readonly record struct RankedValue(int Value, int Start, bool Urgent, int Slack, long Score, long Id);

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

            // Xem đồng hồ ở mọi nút (review Phase 3): kiểm tra theo chu kỳ để lọt cây nhỏ hơn một chu kỳ và phần việc giữa
            // hai lần kiểm tra. Đọc đồng hồ rẻ so với một nút.
            _cancellationToken.ThrowIfCancellationRequested();
            if (_deadline.Expired)
            {
                Statistics.TimeLimitReached = true;
                _aborted = true;
                return false;
            }

            _budget.Used++;
            Statistics.NodesExpanded++;
            return true;
        }
    }
}
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Routing;

/// <summary>Một POI trong bài toán lộ trình, kèm các giá trị đã tính sẵn.</summary>
public sealed record RoutingNode(
    GenerationCandidate Candidate,
    int MatrixIndex,
    int VisitMinutes,
    decimal Cost,
    bool IsMandatory,
    int SkipPenalty);

/// <summary>
/// Khung giờ của một POI trong một ngày địa phương, đã đổi ra phút tính từ
/// <see cref="GenerationInput.StartAtUtc"/>.
/// </summary>
internal readonly record struct DayWindow(int DayStart, int DayEnd, bool HasOpening, int Open, int Close);

/// <summary>
/// Dữ liệu bất biến của một lần giải: các nút, khung giờ, thời gian di chuyển và mức phạt.
/// Mọi phép tính thời gian trong bộ giải dùng số nguyên phút để kiểm tra nhanh;
/// <see cref="ItineraryScheduleEvaluator"/> vẫn là bước kiểm tra cuối cùng có thẩm quyền.
/// </summary>
public sealed class RoutingContext
{
    /// <summary>Giá trị trả về của <see cref="StartAt"/> khi không thể bắt đầu tham quan.</summary>
    public const int Infeasible = -1;

    /// <summary>Giá trị trả về của <see cref="LatestStart"/> khi không có giờ bắt đầu hợp lệ.</summary>
    public const int NoLatestStart = int.MinValue;

    private const int RestDurationMinutes = 30;

    private readonly RouteDurationMatrix _matrix;
    private readonly DayWindow[][] _windows;
    private readonly int _transitionBuffer;
    private readonly int _finalReturnBuffer;
    private readonly RestShortcuts? _shortcuts;

    private RoutingContext(
        GenerationInput input,
        RouteDurationMatrix matrix,
        RoutingNode[] nodes,
        DayWindow[][] windows,
        int horizon,
        int transitionBuffer,
        int finalReturnBuffer,
        RestShortcuts? shortcuts)
    {
        Input = input;
        _shortcuts = shortcuts;
        _matrix = matrix;
        Nodes = nodes;
        _windows = windows;
        Horizon = horizon;
        _transitionBuffer = transitionBuffer;
        _finalReturnBuffer = finalReturnBuffer;
        EndMatrixIndex = matrix.PointCount - 1;

        MandatoryNodes = Enumerable.Range(0, nodes.Length).Where(i => nodes[i].IsMandatory).ToArray();
        OptionalNodes = Enumerable.Range(0, nodes.Length)
            .Where(i => !nodes[i].IsMandatory)
            .OrderByDescending(i => nodes[i].SkipPenalty)
            .ThenBy(i => nodes[i].Candidate.Id)
            .ToArray();
        TotalOptionalPenalty = OptionalNodes.Sum(i => nodes[i].SkipPenalty);
    }

    public GenerationInput Input { get; }

    public IReadOnlyList<RoutingNode> Nodes { get; }

    public IReadOnlyList<int> MandatoryNodes { get; }

    /// <summary>Các nút tùy chọn, sắp theo mức phạt giảm dần (tức thứ hạng AI ranking).</summary>
    public IReadOnlyList<int> OptionalNodes { get; }

    public int TotalOptionalPenalty { get; }

    /// <summary>Mốc phút phải về tới điểm kết thúc, đã trừ thời gian dự phòng cho điểm nghỉ.</summary>
    public int Horizon { get; }

    public int TransitionBuffer => _transitionBuffer;

    /// <summary>
    /// Quy đổi thời điểm DateTimeOffset sang số phút theo gốc mô hình (Input.StartAtUtc).
    /// </summary>
    public int ToModelMinute(DateTimeOffset t) =>
        (int)Math.Ceiling((t - Input.StartAtUtc).TotalMinutes - 1e-9);

    public decimal? Budget => Input.BudgetVnd;

    public int StartMatrixIndex => 0;

    public int EndMatrixIndex { get; }

    public static RoutingContext Create(
        GenerationInput input,
        RouteDurationMatrix matrix,
        IReadOnlyList<GenerationCandidate> matrixCandidates,
        IReadOnlyDictionary<long, int> candidateMatrixIndices,
        SchedulingGenerationOptions options,
        MiniRoutingOptions routingOptions,
        bool reserveRestTime = true,
        bool restShortcuts = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(matrixCandidates);
        ArgumentNullException.ThrowIfNull(candidateMatrixIndices);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(routingOptions);

        var mandatory = input.MandatoryPoiIds.ToHashSet();
        var eligible = matrixCandidates
            .Where(candidate => mandatory.Contains(candidate.Id)
                || (!ItineraryScheduleEvaluator.IsQualifiedRestCandidate(candidate)
                    && (!input.BudgetVnd.HasValue || candidate.EstimatedVisitCost.HasValue)))
            .ToArray();

        var maxScore = eligible
            .Where(candidate => !mandatory.Contains(candidate.Id))
            .Select(candidate => candidate.EffectiveDesirabilityScore)
            .DefaultIfEmpty(0m)
            .Max();

        var nodes = eligible
            .Select(candidate => new RoutingNode(
                candidate,
                candidateMatrixIndices[candidate.Id],
                candidate.VisitDurationMinutes,
                candidate.EstimatedVisitCost ?? 0m,
                mandatory.Contains(candidate.Id),
                mandatory.Contains(candidate.Id)
                    ? 0
                    : SkipPenalty(candidate.EffectiveDesirabilityScore, maxScore, routingOptions.MaxSkipPenaltyMinutes)))
            .ToArray();

        // reserveRestTime = false cho mô hình nới lỏng: dùng khi cần chứng minh vô nghiệm (xem CspItinerarySolver).
        var horizon = input.AvailableMinutes
            - (reserveRestTime ? RestReserveMinutes(input, options.TransitionBufferMinutes) : 0);
        var windows = nodes.Select(node => BuildWindows(input, node.Candidate)).ToArray();

        var shortcuts = restShortcuts
            ? RestShortcuts.Create(input, matrix, matrixCandidates, candidateMatrixIndices, options.TransitionBufferMinutes)
            : null;

        return new RoutingContext(
            input,
            matrix,
            nodes,
            windows,
            horizon,
            options.TransitionBufferMinutes,
            options.FinalReturnBufferMinutes,
            shortcuts);
    }

    /// <summary>Phút di chuyển thô theo ma trận, dùng để tính hàm mục tiêu.</summary>
    public int RawTravel(int fromMatrixIndex, int toMatrixIndex) =>
        _shortcuts?.Raw[fromMatrixIndex, toMatrixIndex] ?? _matrix.GetMinutes(fromMatrixIndex, toMatrixIndex);

    /// <summary>Phút từ lúc rời một điểm tới lúc đến điểm tham quan kế tiếp (có buffer).</summary>
    public int Travel(int fromMatrixIndex, int toMatrixIndex) =>
        (_shortcuts?.Time[fromMatrixIndex, toMatrixIndex] ?? _matrix.GetMinutes(fromMatrixIndex, toMatrixIndex)) + _transitionBuffer;

    /// <summary>Phút từ lúc rời một điểm tới lúc về điểm kết thúc (có buffer về đích).</summary>
    public int ReturnTravel(int fromMatrixIndex) =>
        (_shortcuts?.Time[fromMatrixIndex, EndMatrixIndex] ?? _matrix.GetMinutes(fromMatrixIndex, EndMatrixIndex)) + _finalReturnBuffer;

    /// <summary>
    /// Giờ bắt đầu tham quan sớm nhất khi đến <paramref name="node"/> lúc <paramref name="arrival"/>,
    /// theo đúng quy tắc của <see cref="ItineraryScheduleEvaluator"/>: đến sớm thì chờ tới giờ mở cửa,
    /// phải rời đi trước giờ đóng cửa của cùng ngày; nếu ngày đó không còn kịp (đã đóng cửa hoặc không đủ thời gian
    /// tham quan) thì chờ tới giờ mở cửa của ngày kế tiếp còn kịp trong khung chuyến đi.
    /// </summary>
    public int StartAt(int node, int arrival)
    {
        foreach (var window in _windows[node])
        {
            if (arrival >= window.DayEnd || !window.HasOpening)
            {
                continue;
            }

            var start = Math.Max(arrival, window.Open);
            if (start + Nodes[node].VisitMinutes <= window.Close)
            {
                return start;
            }
        }

        return Infeasible;
    }

    /// <summary>
    /// Giờ bắt đầu tham quan muộn nhất, không quá <paramref name="bound"/>, mà vẫn nằm trong
    /// khung giờ mở cửa của <paramref name="node"/>.
    /// </summary>
    public int LatestStart(int node, int bound)
    {
        var windows = _windows[node];
        for (var i = windows.Length - 1; i >= 0; i--)
        {
            var window = windows[i];
            if (!window.HasOpening || window.Open > bound)
            {
                continue;
            }

            var latest = Math.Min(bound, window.Close - Nodes[node].VisitMinutes);
            if (latest >= window.Open)
            {
                return latest;
            }
        }

        return NoLatestStart;
    }

    /// <summary>
    /// Trả về các khung giờ mở cửa [Open, Close] có mở cửa của node (dùng cho tests/chẩn đoán).
    /// </summary>
    public IEnumerable<(int Open, int Close)> OpenWindows(int node)
    {
        foreach (var window in _windows[node])
        {
            if (window.HasOpening)
            {
                yield return (window.Open, window.Close);
            }
        }
    }

    private static int SkipPenalty(decimal score, decimal maxScore, int maxPenalty)
    {
        if (maxScore <= 0m)
        {
            return Math.Max(1, maxPenalty / 2);
        }

        var ratio = Math.Max(0m, score) / maxScore;
        return Math.Max(1, (int)Math.Round(ratio * maxPenalty, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Dành sẵn thời gian cho điểm nghỉ mà <see cref="ItineraryScheduleEvaluator"/> sẽ chèn sau,
    /// để kiểm tra nhanh không quá lạc quan. Đây là ước lượng chặt hơn thực tế (điểm nghỉ có thể không cần, hoặc
    /// ngắn hơn), nên không được dùng để kết luận vô nghiệm; bước kiểm tra cuối vẫn quyết định.
    /// </summary>
    private static int RestReserveMinutes(GenerationInput input, int transitionBuffer) =>
        input.RestPreference switch
        {
            RestPreference.Auto when input.AvailableMinutes >= 300 => RestDurationMinutes + transitionBuffer,
            RestPreference.Frequent => (input.AvailableMinutes / 150) * (RestDurationMinutes + transitionBuffer),
            _ => 0,
        };

    private static DayWindow[] BuildWindows(GenerationInput input, GenerationCandidate candidate)
    {
        var localStart = TimeZoneInfo.ConvertTime(input.StartAtUtc, input.TimeZone);
        var dayCount = (int)((localStart.TimeOfDay.TotalMinutes + input.AvailableMinutes) / 1440) + 1;
        var windows = new DayWindow[dayCount];

        for (var day = 0; day < dayCount; day++)
        {
            var date = localStart.Date.AddDays(day);
            var dayStart = day == 0 ? int.MinValue : ToMinutes(input, date, ceiling: false);
            var dayEnd = day == dayCount - 1 ? int.MaxValue : ToMinutes(input, date.AddDays(1), ceiling: false);
            var opening = candidate.OpeningHours.FirstOrDefault(hours => hours.DayOfWeek == (byte)date.DayOfWeek);

            windows[day] = opening is null
                ? new DayWindow(dayStart, dayEnd, false, 0, 0)
                : new DayWindow(
                    dayStart,
                    dayEnd,
                    true,
                    ToMinutes(input, date.Add(opening.OpenTime.ToTimeSpan()), ceiling: true),
                    ToMinutes(input, date.Add(opening.CloseTime.ToTimeSpan()), ceiling: false));
        }

        return windows;
    }

    private static int ToMinutes(GenerationInput input, DateTime localTime, bool ceiling)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), input.TimeZone);
        var minutes = (new DateTimeOffset(utc, TimeSpan.Zero) - input.StartAtUtc).TotalMinutes;
        return (int)(ceiling ? Math.Ceiling(minutes) : Math.Floor(minutes));
    }

    /// <summary>
    /// Chặng nới lỏng cho điểm nghỉ là địa điểm thật (R9). <see cref="ItineraryScheduleEvaluator"/> chèn điểm nghỉ R vào
    /// giữa chặng a → b sau một điểm tham quan, nên lịch thật đi a → R → b: phút di chuyển a → R + R → b, và thời gian
    /// a → R + buffer + 30 phút nghỉ + R → b. Trên ma trận không metric, đường vòng đó có thể ngắn hơn đi thẳng, tức lịch
    /// thật tới nơi sớm hơn và đi ít hơn mô hình. Lấy chặng ngắn hơn trong hai cách (bỏ qua giờ mở cửa của điểm nghỉ và
    /// số lần nghỉ cho phép) cho đúng một mô hình nới lỏng: mọi lịch evaluator chấp nhận đều khả thi trên mô hình này, và
    /// phút di chuyển của mô hình không vượt phút của lịch thật. Không có đường vòng nào ngắn hơn thì chặng giữ nguyên.
    /// </summary>
    private sealed class RestShortcuts(int[,] raw, int[,] time)
    {
        /// <summary>Phút di chuyển thô ngắn nhất (đi thẳng hoặc vòng qua một điểm nghỉ).</summary>
        public int[,] Raw { get; } = raw;

        /// <summary>Thời gian chặng ngắn nhất, chưa gồm buffer của điểm đến (đi vòng thì đã gồm buffer và thời gian nghỉ).</summary>
        public int[,] Time { get; } = time;

        public static RestShortcuts Create(
            GenerationInput input,
            RouteDurationMatrix matrix,
            IReadOnlyList<GenerationCandidate> matrixCandidates,
            IReadOnlyDictionary<long, int> candidateMatrixIndices,
            int transitionBuffer)
        {
            var points = matrix.PointCount;
            var rests = matrixCandidates
                .Where(candidate => !input.MandatoryPoiIds.Contains(candidate.Id)
                    && ItineraryScheduleEvaluator.IsQualifiedRestCandidate(candidate))
                .Select(candidate => candidateMatrixIndices[candidate.Id])
                .ToArray();
            var raw = new int[points, points];
            var time = new int[points, points];
            for (var from = 0; from < points; from++)
            {
                for (var to = 0; to < points; to++)
                {
                    var direct = matrix.GetMinutes(from, to);
                    raw[from, to] = direct;
                    time[from, to] = direct;

                    // Evaluator chỉ nghỉ sau một điểm tham quan, không bao giờ trên chặng đầu tiên từ điểm xuất phát.
                    if (from == 0 || from == to)
                    {
                        continue;
                    }

                    foreach (var rest in rests)
                    {
                        if (rest == from || rest == to)
                        {
                            continue;
                        }

                        var first = matrix.GetMinutes(from, rest);
                        var second = matrix.GetMinutes(rest, to);
                        raw[from, to] = Math.Min(raw[from, to], first + second);
                        time[from, to] = Math.Min(time[from, to], first + transitionBuffer + RestDurationMinutes + second);
                    }
                }
            }

            return new RestShortcuts(raw, time);
        }
    }
}
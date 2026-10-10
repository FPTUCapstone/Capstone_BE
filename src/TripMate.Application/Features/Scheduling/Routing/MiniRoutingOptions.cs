namespace TripMate.Application.Features.Scheduling.Routing;

/// <summary>
/// Tham số của bộ giải lộ trình thu gọn (mini routing), tái hiện có chọn lọc các kỹ thuật
/// của OR-Tools Routing Solver cho bài toán TripMate.
/// </summary>
public sealed class MiniRoutingOptions
{
    public const string SectionName = "SchedulingGeneration:MiniRouting";

    /// <summary>
    /// Mức phạt (quy ra phút) khi bỏ qua điểm tùy chọn có điểm phù hợp cao nhất. Các điểm khác
    /// bị phạt theo tỉ lệ <c>EffectiveDesirabilityScore / điểm cao nhất</c>, nên thang điểm AI
    /// ranking thay đổi cũng không làm lệch hàm mục tiêu.
    /// </summary>
    public int MaxSkipPenaltyMinutes { get; init; } = 150;

    /// <summary>Bật Guided Local Search sau khi đã tới cực trị cục bộ.</summary>
    public bool EnableGuidedLocalSearch { get; init; } = true;

    /// <summary>
    /// Hệ số a trong lambda = a × mục tiêu tại cực trị cục bộ đầu tiên / số cạnh của lộ trình
    /// (cách OR-Tools chọn lambda cho GLS).
    /// </summary>
    public double GlsLambdaFactor { get; init; } = 0.1;

    /// <summary>
    /// Số vòng phạt cạnh tối đa của Guided Local Search: điều kiện dừng chính, giúp kết quả tất định.
    /// </summary>
    public int MaxGlsIterations { get; init; } = 50;

    /// <summary>
    /// Lưới an toàn về thời gian (mili giây) khi máy quá tải. Chạm mốc này thì kết quả không còn tất định.
    /// </summary>
    public int TimeLimitMilliseconds { get; init; } = 5000;

    /// <summary>Độ dài đoạn lớn nhất mà toán tử Or-opt được dời (2 hoặc 3 là chuẩn).</summary>
    public int MaxOrOptSegmentLength { get; init; } = 3;

    /// <summary>
    /// Số lời giải tốt nhất (khác nhau) được đưa qua <c>ItineraryScheduleEvaluator</c> ở bước
    /// kiểm tra cuối. Lời giải đầu tiên vượt qua được trả về.
    /// </summary>
    public int FinalCandidatesToValidate { get; init; } = 5;
}
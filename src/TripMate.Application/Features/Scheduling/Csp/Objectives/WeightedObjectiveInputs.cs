using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Các mảng dữ liệu tiền xử lý và tham số đầu vào cho hàm mục tiêu cá nhân hóa (Personalized Weighted Utility).
/// Được chuẩn bị trước lượt tìm kiếm để vòng lặp duyệt hoàn toàn không cấp phát (zero-allocation).
/// </summary>
public sealed class WeightedObjectiveInputs
{
    public const long Scale = 10_000;

    /// <summary>Base utility score per node index ∈ [0, 1], including stability bonus if any.</summary>
    public required double[] Base { get; init; }

    /// <summary>Mapping from node index to dense diversity discount group index.</summary>
    public required int[] DiversityGroupIndex { get; init; }

    /// <summary>Total distinct diversity discount groups.</summary>
    public required int DiversityGroupCount { get; init; }

    /// <summary>Precomputed diversity discount table: [node][k] = round(γ_node^k * Scale).</summary>
    public required long[][] GammaScaledByNode { get; init; }

    /// <summary>Đánh dấu node có phải là địa điểm ngoài trời chịu ảnh hưởng thời tiết/nắng nóng.</summary>
    public required bool[] IsExposedOutdoor { get; init; }

    /// <summary>Đánh dấu node là điểm ăn uống (Food &amp; Drink).</summary>
    public required bool[] IsFood { get; init; }

    /// <summary>Đánh dấu node là điểm dừng nghỉ ngơi (Rest stop).</summary>
    public required bool[] IsRestStop { get; init; }

    /// <summary>Nỗ lực thể lực tiêu hao tại node (tính theo phút hoạt động thể lực).</summary>
    public required int[] Effort { get; init; }

    /// <summary>Bộ tính toán hệ số phù hợp thời gian trong ngày (TimeFit).</summary>
    public required ITimeFitProvider TimeFit { get; init; }

    /// <summary>Tham số cá nhân hóa của du khách.</summary>
    public required PersonalParams P { get; init; }
}
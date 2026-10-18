using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Các mảng dữ liệu tiền xử lý và tham số đầu vào cho hàm mục tiêu cá nhân hóa (Personalized Weighted Utility).
/// Được chuẩn bị trước lượt tìm kiếm để vòng lặp duyệt hoàn toàn không cấp phát (zero-allocation).
/// </summary>
public sealed class WeightedObjectiveInputs
{
    public const long Scale = 10_000;

    /// <summary>Điểm lợi ích cơ sở theo node index ∈ [0, 1], đã cộng stability bonus nếu có.</summary>
    public required double[] Base { get; init; }

    /// <summary>Ánh xạ từ node index sang chỉ số danh mục dày đặc (dense category index).</summary>
    public required int[] CategoryIndex { get; init; }

    /// <summary>Tổng số danh mục phân biệt.</summary>
    public required int CategoryCount { get; init; }

    /// <summary>Hệ số đa dạng danh mục tính sẵn: [cat][k] = round(γ_cat^k * Scale).</summary>
    public required long[][] GammaScaled { get; init; }

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

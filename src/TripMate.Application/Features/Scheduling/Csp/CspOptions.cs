namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>Tham số của bộ giải CSP lập lịch TripMate.</summary>
public sealed class CspOptions
{
    public const string SectionName = "SchedulingGeneration:Csp";

    /// <summary>k: số vị trí tối đa trong lộ trình (số biến S1..Sk).</summary>
    public int MaxStops { get; init; } = 10;

    /// <summary>N: số điểm tùy chọn xếp hạng cao nhất được đưa vào miền giá trị.</summary>
    public int MaxOptionalDomainSize { get; init; } = 20;

    /// <summary>
    /// Giới hạn số nút của cây tìm kiếm: đây là điều kiện dừng chính. Chạm giới hạn thì trả về lời giải tốt nhất đã có.
    /// Dừng theo số nút nên cùng một yêu cầu luôn cho cùng một lịch trình, dù máy nhanh hay chậm. Khi có điểm nghỉ,
    /// lần duyệt mô hình có dự phòng nghỉ và lần duyệt mô hình nới lỏng dùng chung giới hạn này.
    /// </summary>
    public int MaxNodes { get; init; } = 200_000;

    /// <summary>
    /// Lưới an toàn về thời gian (mili giây), chỉ để chặn trường hợp máy quá tải. Đặt rộng hơn nhiều so với thời gian
    /// duyệt <see cref="MaxNodes"/> nút; nếu thường xuyên chạm mốc này thì kết quả không còn tất định, nên giảm MaxNodes.
    /// </summary>
    public int TimeLimitMilliseconds { get; init; } = 5000;

    /// <summary>
    /// Số lời giải tốt nhất của mỗi lần duyệt được đưa qua bước kiểm tra cuối (chèn điểm nghỉ). Khi có điểm nghỉ, mọi
    /// lời giải này đều được kiểm tra và lịch có mục tiêu thật nhỏ nhất được chọn.
    /// </summary>
    public int FinalCandidatesToValidate { get; init; } = 5;

    /// <summary>
    /// Dựng nhanh một lời giải chèn rẻ nhất làm cận trên ban đầu cho branch and bound.
    /// Không đổi tính đúng của CSP (vẫn duyệt và cắt tỉa như cũ), chỉ giúp cắt nhánh sớm hơn.
    /// </summary>
    public bool UseInitialIncumbent { get; init; } = true;

    /// <summary>
    /// Sau CSP, chạy thêm tìm kiếm cục bộ (2-opt, Or-opt, GLS…) trong thời gian còn lại.
    /// Tắt mặc định để kết quả là CSP thuần; bật khi ưu tiên chất lượng lộ trình.
    /// </summary>
    public bool PolishWithLocalSearch { get; init; }
}
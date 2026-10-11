namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>Tham số của bộ giải CSP lập lịch TripMate.</summary>
public sealed class CspOptions
{
    public const string SectionName = "SchedulingGeneration:Csp";

    /// <summary>
    /// k: giới hạn hành chính cho số vị trí trong lộ trình (số biến S1..Sk). Null (mặc định): không giới hạn, số điểm dừng
    /// chỉ bị chặn bởi các ràng buộc cứng (R2).
    /// </summary>
    public int? MaxStops { get; init; }

    /// <summary>
    /// N: số điểm tùy chọn xếp hạng cao nhất (theo thứ hạng chuẩn của product rule) được đưa vào miền giá trị. Null (mặc
    /// định): mọi điểm tùy chọn mà heuristic cũng xét, tức tối đa <c>MaxMatrixCandidates</c> (R3).
    /// </summary>
    public int? MaxOptionalDomainSize { get; init; }

    /// <summary>
    /// Giới hạn số nút của cây tìm kiếm: đây là điều kiện dừng chính. Chạm giới hạn thì trả về lời giải tốt nhất đã có.
    /// Dừng theo số nút nên cùng một yêu cầu cho cùng một lịch trình dù máy nhanh hay chậm, miễn là lưới an toàn
    /// <see cref="TimeLimitMilliseconds"/> không bị chạm. Khi có điểm nghỉ, lần duyệt mô hình có dự phòng nghỉ và lần duyệt
    /// mô hình nới lỏng dùng chung giới hạn này.
    /// </summary>
    public int MaxNodes { get; init; } = 200_000;

    /// <summary>
    /// Lưới an toàn về thời gian (mili giây), chỉ để chặn trường hợp máy quá tải; mặc định 2.500 ms theo Report 3 V2. Duyệt
    /// <see cref="MaxNodes"/> nút chỉ mất vài trăm mili giây, nên bình thường không bao giờ chạm mốc này. Khi chạm, kết quả
    /// CSP dở dang phụ thuộc tốc độ máy nên bị bỏ, và bên gọi trả lịch heuristic nguyên vẹn (R5): người dùng không bao giờ
    /// nhận một kết quả dở dang. Việc mốc có bị chạm hay không vẫn phụ thuộc máy, nên trên máy chậm hoặc quá tải, cùng một
    /// yêu cầu có thể nhận lịch heuristic ở chỗ máy nhanh nhận lịch CSP (không bao giờ tệ hơn lịch heuristic).
    /// </summary>
    public int TimeLimitMilliseconds { get; init; } = 2500;

    /// <summary>
    /// Số lời giải tốt nhất (theo product rule trên mô hình) của mỗi lần duyệt được đưa qua bước kiểm tra cuối (chèn điểm
    /// nghỉ). Lịch được chọn bằng <c>ScheduleGlobalComparator</c> trên kết quả của evaluator.
    /// </summary>
    public int FinalCandidatesToValidate { get; init; } = 5;

    /// <summary>
    /// Sau CSP, chạy thêm tìm kiếm cục bộ (2-opt, Or-opt, GLS…) trong thời gian còn lại.
    /// Tắt mặc định để kết quả là CSP thuần; bật khi ưu tiên chất lượng lộ trình.
    /// </summary>
    public bool PolishWithLocalSearch { get; init; }
}
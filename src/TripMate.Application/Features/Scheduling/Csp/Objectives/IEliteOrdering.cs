using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Quy tắc sắp xếp và chấp nhận các lời giải vào <see cref="EliteSet"/>.
/// Tách rời logic so sánh mục tiêu khỏi cấu trúc dữ liệu tập ưu tú.
/// </summary>
internal interface IEliteOrdering
{
    /// <summary>
    /// Kiểm tra rẻ trước khi dựng <see cref="RouteState"/>: trả về true nếu candidate có cơ hội tốt hơn hoặc bằng phần tử tệ nhất hiện tại.
    /// </summary>
    bool MayAccept(in EliteKey candidate, in EliteKey worst);

    /// <summary>
    /// So sánh đầy đủ hai lời giải (bao gồm cả thứ tự điểm trong <see cref="RouteState"/> để loại trùng).
    /// Trả về số âm nếu candidate tốt hơn existing, số dương nếu kém hơn, 0 nếu trùng lặp hoàn toàn.
    /// </summary>
    int Compare(RouteState candidateState, in EliteKey candidateKey, RouteState existingState, in EliteKey existingKey);

    /// <summary>
    /// Tạo <see cref="EliteKey"/> từ một <see cref="RouteState"/> hoàn chỉnh (dùng khi nạp lời giải từ GLS polish hoặc heuristic).
    /// </summary>
    EliteKey CreateKey(RouteState state);
}

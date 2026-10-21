namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Incumbent không phụ thuộc kiểu kernel cụ thể. SearchRun và Solve chỉ chuyển token, không đọc nội dung.
/// </summary>
internal abstract class IncumbentToken;
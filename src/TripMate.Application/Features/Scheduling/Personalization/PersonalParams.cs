namespace TripMate.Application.Features.Scheduling.Personalization;

/// <summary>
/// Các tham số cá nhân hóa được tạo từ hồ sơ du khách (TravelerProfile) và cấu hình PersonalizationOptions có version.
/// Được sử dụng bởi các thuật toán lập lịch cá nhân hóa (Personalized Weighted Utility).
/// </summary>
/// <param name="MaxDailyEffort">Nỗ lực thể lực tối đa khuyến nghị mỗi ngày tính theo phút (phút tham quan + di chuyển).</param>
/// <param name="RhoEffortPerHour">Trọng số phạt khi vượt ngưỡng nỗ lực; được chuẩn hóa theo giờ (hoặc 60 phút) để tránh biến soft penalty thành hard constraint.</param>
public sealed record PersonalParams(
    double LambdaPerMin,
    double Mu,
    int MaxStopsPerDay,
    int MaxDailyEffort,
    int RestAfterMin,
    int MaxConsecutiveOutdoor,
    double SunriseFactor,
    double GammaDefault,
    double GammaLoved,
    IReadOnlySet<string> LovedCategories,
    double RhoOutdoor,
    double RhoRest,
    double RhoStops,
    double RhoEffortPerHour,
    string ConfigVersion);

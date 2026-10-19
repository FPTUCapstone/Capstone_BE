namespace TripMate.Application.Features.Scheduling.Shadow;

/// <summary>
/// Thống kê và độ lệch (divergence) của lần chạy shadow so với lịch chính.
/// </summary>
public sealed record ShadowMetrics(
    long RequestId,
    bool Success,
    bool TimedOut,
    double ElapsedMs,
    long? ModelObjective,
    bool IsSequenceIdentical,
    double JaccardSimilarity,
    int AddedStopsCount,
    int RemovedStopsCount,
    int DurationDiffMinutes,
    decimal CostDiffVnd,
    string? Error);

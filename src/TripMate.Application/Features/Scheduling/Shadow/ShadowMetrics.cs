using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Shadow;

/// <summary>
/// Thống kê và độ lệch (divergence) của lần chạy shadow so với lịch chính. <see cref="Pace"/> là nhịp độ mà hàm mục tiêu
/// shadow đã dùng, để so số điểm theo nhịp độ (ADR-002, gate 2).
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
    string? Error,
    TravelerPace Pace);
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Common;

/// <summary>Bộ giải dùng để tìm thứ tự điểm đến.</summary>
public enum SchedulingSolverMode
{
    /// <summary>Thuật toán hiện tại: <see cref="OptionalRouteOptimizer"/>.</summary>
    Heuristic = 0,

    /// <summary>
    /// Bộ giải lộ trình thu gọn (<see cref="MiniRoutingSolver"/>); không tìm được lời giải thì
    /// tự quay về <see cref="Heuristic"/>.
    /// </summary>
    MiniRouting = 1,

    /// <summary>
    /// Bộ giải CSP (<see cref="CspItinerarySolver"/>): backtracking, forward checking, branch and bound.
    /// CSP không tìm được lời giải thì dùng <see cref="Heuristic"/>; chỉ khi cả hai đều thất bại mới báo lỗi,
    /// kèm tên các điểm bắt buộc gây xung đột nhiều nhất mà CSP xác định được.
    /// </summary>
    Csp = 2,
}

public sealed class SchedulingGenerationOptions
{
    public const string SectionName = "SchedulingGeneration";
    public const int DefaultMaxMatrixCandidates = 40;
    public const int MinimumMaxMatrixCandidates = 6;

    public const int DefaultMaxRouteOptimizationSeeds = 3;
    public const int MinRouteOptimizationSeeds = 1;
    public const int MaxAllowedRouteOptimizationSeeds = 10;

    public const int DefaultMaxRouteEvaluations = 5000;
    public const int MinRouteEvaluations = 100;
    public const int MaxAllowedRouteEvaluations = 50000;

    public int TransitionBufferMinutes { get; init; } = 10;

    public int FinalReturnBufferMinutes { get; init; } = 15;

    public int MaxMatrixCandidates { get; init; } = DefaultMaxMatrixCandidates;

    public bool EnableOptionalRouteOptimization { get; init; } = true;

    public int MaxRouteOptimizationSeeds { get; init; } = DefaultMaxRouteOptimizationSeeds;

    public int MaxRouteEvaluations { get; init; } = DefaultMaxRouteEvaluations;

    public SchedulingSolverMode SolverMode { get; init; } = SchedulingSolverMode.Heuristic;

    public MiniRoutingOptions MiniRouting { get; init; } = new();

    public CspOptions Csp { get; init; } = new();

    public int EffectiveMaxMatrixCandidates => MaxMatrixCandidates >= MinimumMaxMatrixCandidates
        ? MaxMatrixCandidates
        : DefaultMaxMatrixCandidates;
}
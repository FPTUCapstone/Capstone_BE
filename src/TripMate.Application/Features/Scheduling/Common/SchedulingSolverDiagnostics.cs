using System.Diagnostics;

using TripMate.Application.Features.Scheduling.Csp;

namespace TripMate.Application.Features.Scheduling.Common;

/// <summary>Bộ giải thực sự tạo ra lịch trình trong một lần sinh, kể cả khi đã chuyển sang thuật toán dự phòng.</summary>
public static class SchedulingSolverOutcomes
{
    public const string Heuristic = "heuristic";
    public const string Csp = "csp";
    public const string MiniRouting = "mini_routing";

    /// <summary>CSP duyệt hết cây mô hình nới lỏng mà không có lời giải; thuật toán heuristic vẫn tìm được lịch.</summary>
    public const string HeuristicFallbackCspProvedInfeasible = "heuristic_fallback_csp_proved_infeasible";

    /// <summary>CSP có lời giải nhưng không lời giải nào qua bước kiểm tra cuối (chèn điểm nghỉ).</summary>
    public const string HeuristicFallbackCspNoValidCandidate = "heuristic_fallback_csp_no_valid_candidate";

    /// <summary>CSP chạm giới hạn số nút hoặc thời gian trước khi tìm được lời giải nào.</summary>
    public const string HeuristicFallbackCspSearchLimit = "heuristic_fallback_csp_search_limit";

    /// <summary>MiniRouting không trả về lời giải hợp lệ.</summary>
    public const string HeuristicFallbackMiniRouting = "heuristic_fallback_mini_routing";

    /// <summary>Không bộ giải nào tìm được lịch trình.</summary>
    public const string Infeasible = "infeasible";
}

/// <summary>
/// Ghi lại bộ giải đã tạo ra lịch trình, để benchmark và giám sát đếm đúng số lần chuyển sang thuật toán dự phòng
/// thay vì suy đoán từ số liệu của bộ giải. Dùng nguồn riêng để không lẫn với
/// <see cref="RouteOptimizationDiagnostics"/>.
/// </summary>
public static class SchedulingSolverDiagnostics
{
    public const string ActivitySourceName = "TripMate.Scheduling.Solver";
    public const string OutcomeActivityName = "SchedulingSolverOutcome";
    public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    public static void RecordOutcome(SchedulingSolverMode mode, string outcome, CspStatistics? csp)
    {
        using var activity = Source.StartActivity(OutcomeActivityName);
        if (activity is null) return;

        activity.SetTag("solver.mode", mode.ToString());
        activity.SetTag("solver.outcome", outcome);
        if (csp is null) return;

        activity.SetTag("csp.nodes_expanded", csp.NodesExpanded);
        activity.SetTag("csp.solutions_found", csp.SolutionsFound);
        activity.SetTag("csp.search_completed", csp.SearchCompleted);
        activity.SetTag("csp.node_limit_reached", csp.NodeLimitReached);
        activity.SetTag("csp.time_limit_reached", csp.TimeLimitReached);
        activity.SetTag("csp.used_relaxed_rest_model", csp.UsedRelaxedRestModel);
        activity.SetTag("csp.elapsed_ms", csp.Elapsed.TotalMilliseconds);
    }
}
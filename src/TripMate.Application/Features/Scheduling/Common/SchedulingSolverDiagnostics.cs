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

    /// <summary>
    /// Lưới an toàn thời gian của CSP bị chạm (R5): kết quả CSP dở dang phụ thuộc tốc độ máy nên bị bỏ, lịch heuristic
    /// được trả về nguyên vẹn.
    /// </summary>
    public const string HeuristicFallbackCspTimeLimit = "heuristic_fallback_csp_time_limit";

    /// <summary>MiniRouting không trả về lời giải hợp lệ.</summary>
    public const string HeuristicFallbackMiniRouting = "heuristic_fallback_mini_routing";

    /// <summary>Không bộ giải nào tìm được lịch trình.</summary>
    public const string Infeasible = "infeasible";
}

/// <summary>
/// Loại bộ giải đã tạo ra lịch trình (R8): quy đổi từ <see cref="SchedulingSolverOutcomes"/>, không phải một cách phân
/// loại riêng. <see cref="Heuristic"/>, <see cref="Csp"/> và <see cref="CspFallback"/> là ba giá trị của spec;
/// <see cref="MiniRouting"/> là phần mở rộng cho chế độ thử nghiệm, để lịch của nó không bị gộp vào hai loại kia.
/// </summary>
public static class SchedulingSolverKinds
{
    public const string Heuristic = "Heuristic";
    public const string Csp = "Csp";
    public const string CspFallback = "CspFallback";
    public const string MiniRouting = "MiniRouting";

    /// <summary>Loại bộ giải của một outcome; null khi không có lịch (<see cref="SchedulingSolverOutcomes.Infeasible"/>).</summary>
    public static string? Of(string outcome) => outcome switch
    {
        SchedulingSolverOutcomes.Heuristic or SchedulingSolverOutcomes.HeuristicFallbackMiniRouting => Heuristic,
        SchedulingSolverOutcomes.Csp => Csp,
        SchedulingSolverOutcomes.HeuristicFallbackCspProvedInfeasible
            or SchedulingSolverOutcomes.HeuristicFallbackCspNoValidCandidate
            or SchedulingSolverOutcomes.HeuristicFallbackCspSearchLimit
            or SchedulingSolverOutcomes.HeuristicFallbackCspTimeLimit => CspFallback,
        SchedulingSolverOutcomes.MiniRouting => MiniRouting,
        _ => null,
    };
}

/// <summary>
/// Ghi lại bộ giải đã tạo ra lịch trình, để benchmark và giám sát đếm đúng số lần chuyển sang thuật toán dự phòng
/// thay vì suy đoán từ số liệu của bộ giải. Dùng nguồn riêng để không lẫn với
/// <see cref="RouteOptimizationDiagnostics"/>. Chỉ chứa số liệu tổng hợp, không bao giờ chứa danh tính người dùng hay
/// POI (R8); test giữ danh sách tag được phép.
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
        if (SchedulingSolverKinds.Of(outcome) is { } kind)
        {
            activity.SetTag("solver.kind", kind);
        }

        if (csp is null) return;

        activity.SetTag("csp.nodes_expanded", csp.NodesExpanded);
        activity.SetTag("csp.solutions_found", csp.SolutionsFound);
        activity.SetTag("csp.pruned_by_bound", csp.PrunedByBound);
        activity.SetTag("csp.pruned_by_constraints", csp.PrunedByConstraints);
        activity.SetTag("csp.pruned_by_forward_checking", csp.PrunedByForwardChecking);
        activity.SetTag("csp.search_completed", csp.SearchCompleted);
        activity.SetTag("csp.node_limit_reached", csp.NodeLimitReached);
        activity.SetTag("csp.time_limit_reached", csp.TimeLimitReached);
        activity.SetTag("csp.used_relaxed_rest_model", csp.UsedRelaxedRestModel);
        activity.SetTag("csp.chose_relaxed_rest_model", csp.ChoseRelaxedRestModel);

        // Outcome csp với tag này true nghĩa là CSP chỉ xác nhận lịch heuristic, không tạo ra lịch mới: giám sát đếm phần
        // CSP cải thiện được bằng outcome csp và tag false.
        activity.SetTag("csp.returned_heuristic_plan", csp.ReturnedInitialIncumbent);
        activity.SetTag("csp.evaluator_calls", csp.EvaluatorCalls);
        activity.SetTag("csp.in_search_evaluation_limit_reached", csp.InSearchEvaluationLimitReached);
        activity.SetTag("csp.tie_evaluation_limit_reached", csp.TieEvaluationLimitReached);
        activity.SetTag("csp.elapsed_ms", csp.Elapsed.TotalMilliseconds);
    }
}
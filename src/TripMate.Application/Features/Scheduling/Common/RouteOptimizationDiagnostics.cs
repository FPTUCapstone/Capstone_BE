using System.Diagnostics;

namespace TripMate.Application.Features.Scheduling.Common;

public static class RouteOptimizationDiagnostics
{
    public const string ActivitySourceName = "TripMate.Scheduling.Optimization";
    public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    public static void RecordOptimization(
        bool enabled,
        int baselineOptionalCount,
        int finalOptionalCount,
        int baselineTravelMinutes,
        int finalTravelMinutes,
        int evaluationsCount,
        int seedCount,
        int reconsideredAdmissionsCount,
        int twoOptMovesCount,
        int relocateMovesCount,
        bool budgetExhausted,
        TimeSpan elapsedOptimization)
    {
        using var activity = Source.StartActivity("OptionalRouteOptimization");
        if (activity is null) return;

        activity.SetTag("optimization.enabled", enabled);
        activity.SetTag("optimization.baseline_optional_count", baselineOptionalCount);
        activity.SetTag("optimization.final_optional_count", finalOptionalCount);
        activity.SetTag("optimization.baseline_travel_minutes", baselineTravelMinutes);
        activity.SetTag("optimization.final_travel_minutes", finalTravelMinutes);

        var travelDelta = finalTravelMinutes - baselineTravelMinutes;
        activity.SetTag("optimization.travel_delta_minutes", travelDelta);

        if (baselineTravelMinutes > 0)
        {
            var pctChange = Math.Round((double)travelDelta / baselineTravelMinutes * 100.0, 2);
            activity.SetTag("optimization.travel_percent_change", pctChange);
        }

        activity.SetTag("optimization.evaluations_count", evaluationsCount);
        activity.SetTag("optimization.seed_count", seedCount);
        activity.SetTag("optimization.reconsidered_admissions_count", reconsideredAdmissionsCount);
        activity.SetTag("optimization.two_opt_moves_count", twoOptMovesCount);
        activity.SetTag("optimization.relocate_moves_count", relocateMovesCount);
        activity.SetTag("optimization.budget_exhausted", budgetExhausted);
        activity.SetTag("optimization.elapsed_ms", elapsedOptimization.TotalMilliseconds);
    }
}
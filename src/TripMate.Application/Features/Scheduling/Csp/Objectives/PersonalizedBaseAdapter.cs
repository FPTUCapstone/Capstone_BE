using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Adapter tạm thời chuẩn hóa điểm TravelerPreferenceScoring sang Base score ∈ [0, 1].
/// Phục vụ chế độ Personalized Weighted Utility khi chưa có điểm từ mô hình AI chuyên biệt.
/// </summary>
public static class PersonalizedBaseAdapter
{
    public static double[] CreateNormalizedBase(RoutingContext ctx)
    {
        var n = ctx.Nodes.Count;
        var scores = new double[n];
        var min = double.MaxValue;
        var max = double.MinValue;

        for (var i = 0; i < n; i++)
        {
            var score = ctx.Nodes[i].Candidate.PreferenceScore;
            scores[i] = score;
            if (score < min) min = score;
            if (score > max) max = score;
        }

        var range = max - min;
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            result[i] = range < 0.05 ? 0.5 : (scores[i] - min) / range;
        }

        return result;
    }
}

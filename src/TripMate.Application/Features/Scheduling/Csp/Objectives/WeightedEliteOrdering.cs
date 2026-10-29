using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Quy tắc sắp xếp và duy trì EliteSet theo hàm mục tiêu đa tiêu chí có trọng số (Personalized Weighted Utility).
/// Cấp 1: Điểm lợi ích mục tiêu (Utility) cao hơn xếp trước.
/// Cấp 2: Phút di chuyển theo ma trận (Travel) ít hơn xếp trước.
/// Cấp 3: Thời điểm kết thúc (Finish) sớm hơn xếp trước.
/// Cấp 4: Dãy thứ tự các POI (Sequence) để loại trừ trùng lặp và đảm bảo tính tất định.
/// </summary>
internal sealed class WeightedEliteOrdering(
    WeightedObjectiveInputs inputs,
    RoutingContext ctx,
    int[] groupScratch) : IEliteOrdering
{
    public bool MayAccept(in EliteKey candidate, in EliteKey worst)
    {
        if (candidate.Utility > worst.Utility)
        {
            return true;
        }

        if (candidate.Utility < worst.Utility)
        {
            return false;
        }

        if (candidate.Travel < worst.Travel)
        {
            return true;
        }

        if (candidate.Travel > worst.Travel)
        {
            return false;
        }

        return candidate.Finish <= worst.Finish;
    }

    public int Compare(RouteState candidateState, in EliteKey candidateKey, RouteState existingState, in EliteKey existingKey)
    {
        // Cấp 1: Utility cao hơn xếp trước (trả về số âm nếu candidate tốt hơn existing)
        var cmp = existingKey.Utility.CompareTo(candidateKey.Utility);
        if (cmp != 0)
        {
            return cmp;
        }

        // Cấp 2: Travel ít hơn xếp trước
        cmp = candidateKey.Travel.CompareTo(existingKey.Travel);
        if (cmp != 0)
        {
            return cmp;
        }

        // Cấp 3: Finish sớm hơn xếp trước
        cmp = candidateKey.Finish.CompareTo(existingKey.Finish);
        if (cmp != 0)
        {
            return cmp;
        }

        // Cấp 4: Dãy Id của POI để phân biệt các lộ trình khác nhau và đồng bộ tuyệt đối với Incumbent
        var minLen = Math.Min(candidateState.Sequence.Count, existingState.Sequence.Count);
        for (var i = 0; i < minLen; i++)
        {
            var idCandidate = ctx.Nodes[candidateState.Sequence[i]].Candidate.Id;
            var idExisting = ctx.Nodes[existingState.Sequence[i]].Candidate.Id;
            var seqCmp = idCandidate.CompareTo(idExisting);
            if (seqCmp != 0)
            {
                return seqCmp;
            }
        }

        return candidateState.Sequence.Count.CompareTo(existingState.Sequence.Count);
    }

    public EliteKey CreateKey(RouteState state)
    {
        var j = ObjectiveCalculator.Replay(inputs, ctx, state.Sequence, state.Earliest, groupScratch);
        return new EliteKey((double)j / WeightedObjectiveInputs.Scale, state.TravelMinutes, state.Finish);
    }
}
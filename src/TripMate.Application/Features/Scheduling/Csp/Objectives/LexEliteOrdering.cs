using TripMate.Application.Features.Scheduling.Routing;

namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Quy tắc sắp xếp ưu tú theo thứ tự từ điển chuẩn (Lexicographic / Product Rule).
/// Cấp 1: Vector bao gồm (Inclusion) giảm dần theo hạng.
/// Cấp 2: Phút di chuyển theo ma trận (Travel) tăng dần.
/// Cấp 3: Thời điểm kết thúc (Finish) tăng dần.
/// Cấp 4: Dãy Id của POI (Sequence) để phân biệt các lộ trình khác nhau có cùng chi phí và thời lượng.
/// </summary>
internal sealed class LexEliteOrdering(CspProductRule rule) : IEliteOrdering
{
    public bool MayAccept(in EliteKey candidate, in EliteKey worst)
    {
        var comparison = CspProductRule.CompareInclusion(candidate.Inclusion!, worst.Inclusion!);
        if (comparison != 0)
        {
            return comparison < 0;
        }

        comparison = candidate.Travel.CompareTo(worst.Travel);
        return comparison != 0 ? comparison < 0 : candidate.Finish <= worst.Finish;
    }

    public int Compare(RouteState candidateState, in EliteKey candidateKey, RouteState existingState, in EliteKey existingKey)
    {
        return rule.Compare(candidateState, candidateKey.Inclusion!, existingState, existingKey.Inclusion!);
    }

    public EliteKey CreateKey(RouteState state)
    {
        return new EliteKey(rule.Inclusion(state.Sequence), state.TravelMinutes, state.Finish);
    }
}
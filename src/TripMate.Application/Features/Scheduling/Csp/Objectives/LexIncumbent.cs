namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Đại diện incumbent dạng token cho LexicographicKernel: lưu trữ vector bao gồm (Inclusion) và phút di chuyển (Travel).
/// Hỗ trợ dùng chung tham chiếu đối tượng giữa primary và relaxed run.
/// </summary>
internal sealed class LexIncumbent(ulong[] inclusion, int travel) : IncumbentToken
{
    public ulong[] Inclusion { get; set; } = inclusion;
    public int Travel { get; set; } = travel;

    public int CompareTo(ulong[] inclusion, int travel)
    {
        var result = CspProductRule.CompareInclusion(inclusion, Inclusion);
        return result != 0 ? result : travel.CompareTo(Travel);
    }
}

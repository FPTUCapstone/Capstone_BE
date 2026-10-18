namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Các mảng và giá trị tiền tính toán trước khi bắt đầu duyệt CSP, dùng chung cho các bộ kiểm tra cận
/// và forward checking nhằm đạt hiệu năng tối đa.
/// </summary>
internal sealed class SearchPrecomputed(
    int[] minIncomingTravel,
    int[] minIncomingRaw,
    int[] latestPossibleStart,
    int[] minExit,
    int[] returnRaw,
    int minReturnTravel,
    int mandatoryWeight,
    int[] initialDomain,
    bool[] inInitialDomain)
{
    public int[] MinIncomingTravel { get; } = minIncomingTravel;
    public int[] MinIncomingRaw { get; } = minIncomingRaw;
    public int[] LatestPossibleStart { get; } = latestPossibleStart;
    public int[] MinExit { get; } = minExit;
    public int[] ReturnRaw { get; } = returnRaw;
    public int MinReturnTravel { get; } = minReturnTravel;
    public int MandatoryWeight { get; } = mandatoryWeight;
    public int[] InitialDomain { get; } = initialDomain;
    public bool[] InInitialDomain { get; } = inInitialDomain;
}

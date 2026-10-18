namespace TripMate.Application.Features.Scheduling.Csp;

/// <summary>
/// Bộ đệm lộ trình đang xét trong phép duyệt CSP: mảng flat được tái sử dụng qua các tầng đệ quy
/// để tránh cấp phát bộ nhớ tại mỗi nút (Zero Heap Allocation during Search).
/// </summary>
internal sealed class SearchBuffers
{
    public readonly int[] Sequence;
    public readonly int[] Departure;
    public readonly int[] Travel;
    public readonly decimal[] Cost;
    public readonly bool[] Contains;
    public int Depth;
    public int MandatoryPlaced;

    public SearchBuffers(int maxDepth, int nodeCount)
    {
        Sequence = new int[maxDepth];
        Departure = new int[maxDepth];
        Travel = new int[maxDepth + 1];
        Cost = new decimal[maxDepth + 1];
        Contains = new bool[nodeCount];
    }
}

namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Trạng thái mềm cần theo dõi để tính phạt nỗ lực thể lực, nghỉ ngơi, số điểm dừng, hoạt động ngoài trời.
/// Struct nhỏ truyền bằng ref để đảm bảo zero allocation.
/// </summary>
public struct SoftState
{
    public int ConsecutiveOutdoor;
    public int ActiveSinceRestMin;
    public int Stops;
    public int Effort;
}
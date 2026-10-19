namespace TripMate.Application.Features.Scheduling.Shadow;

/// <summary>
/// Giao diện đẩy yêu cầu chạy shadow ngầm.
/// </summary>
public interface IShadowSchedulingRunner
{
    void Enqueue(ShadowWorkItem item);
}

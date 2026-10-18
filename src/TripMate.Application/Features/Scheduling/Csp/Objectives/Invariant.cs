namespace TripMate.Application.Features.Scheduling.Csp.Objectives;

/// <summary>
/// Kiểm tra bất biến nội bộ của hệ thống lập lịch CSP.
/// </summary>
internal static class Invariant
{
    public static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Invariant violation: {message}");
        }
    }
}

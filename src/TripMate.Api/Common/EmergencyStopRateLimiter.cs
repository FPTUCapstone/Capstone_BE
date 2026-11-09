namespace TripMate.Api.Common;

/// <summary>
/// Per-Operator limit on the password-protected Emergency Stop endpoint. It counts every attempt,
/// so the limit stays high enough for an Operator stopping several departures during one incident.
/// </summary>
public static class EmergencyStopRateLimiter
{
    public const string PolicyName = "emergency-stop";
}

public sealed class EmergencyStopRateLimitOptions
{
    public int PermitLimit { get; set; } = 10;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);
}
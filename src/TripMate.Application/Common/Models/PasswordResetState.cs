namespace TripMate.Application.Common.Models;

public enum PasswordResetDeliveryState
{
    Pending,
    Sent,
    Failed,
    Unknown,
}

/// <summary>
/// Immutable snapshot of the current in-memory password-reset state for one account.
/// Generation is a backend-internal identifier: memory-only, never sent to clients,
/// never persisted, and not an HMAC input — it only guards stale operations against
/// a newer generation.
/// </summary>
public sealed record PasswordResetState(
    long UserId,
    string ProtectedOtp,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int FailedAttemptCount,
    PasswordResetDeliveryState DeliveryState,
    long Generation);
namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Normalizes the response timing of password-reset requests so account-specific outcomes
/// (unknown email vs eligible+SMTP) do not form an obvious reliable account-existence
/// timing oracle. Implementations must never use Thread.Sleep; tests fake this interface.
/// </summary>
public interface IRequestTimingNormalizer
{
    Task EnsureMinimumDurationAsync(DateTimeOffset startedAtUtc, CancellationToken cancellationToken);
}
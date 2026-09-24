using TripMate.Application.Common.Models;

namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Non-blocking handoff for password-reset email delivery. Implementations must keep the raw
/// OTP process-local and transient, and must never persist or log it.
/// </summary>
public interface IPasswordResetEmailQueue
{
    /// <summary>
    /// Attempts to enqueue one delivery without waiting for SMTP. Returns false when the
    /// bounded queue cannot accept the item so the matching reset generation can fail closed.
    /// </summary>
    bool TryEnqueue(PasswordResetEmailDelivery delivery);
}
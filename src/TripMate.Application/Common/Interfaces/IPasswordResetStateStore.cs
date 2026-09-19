using TripMate.Application.Common.Models;

namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Process-local store of password-reset state for UC-06. The current design assumes one
/// authoritative backend process; a restart/redeploy (a new store instance) invalidates all
/// outstanding OTPs. Implementations must be safe for concurrent use and synchronize per account.
/// Timing and attempt rules are owned by <see cref="PasswordResetPolicy"/>.
/// </summary>
public interface IPasswordResetStateStore
{
    /// <summary>
    /// Returns the current usable reset state for the account, or null when no state exists,
    /// it was consumed/invalidated, or it has reached ExpiresAtUtc. Expired state can never
    /// become usable again.
    /// </summary>
    PasswordResetState? GetCurrent(long userId);

    /// <summary>
    /// Creates a new reset generation that supersedes any previous one, using the current
    /// clock for CreatedAtUtc. See the explicit-timestamp overload.
    /// </summary>
    PasswordResetIssueResult Issue(long userId, string protectedOtp);

    /// <summary>
    /// Creates a new reset generation stamped with the caller's captured
    /// <paramref name="createdAtUtc"/> so the caller can bind the protected OTP to exactly
    /// the same creation time the store records. ExpiresAtUtc is exactly
    /// createdAtUtc + <see cref="PasswordResetPolicy.OtpTimeToLive"/>. Issuance is suppressed
    /// by the resend cooldown of <see cref="PasswordResetPolicy.ResendCooldown"/> measured
    /// from the latest in-memory issuance; a suppressed call never replaces or invalidates
    /// the existing current state.
    /// </summary>
    PasswordResetIssueResult Issue(long userId, string protectedOtp, DateTimeOffset createdAtUtc);

    /// <summary>
    /// Transitions the delivery state of the identified generation (Pending to Sent/Failed/Unknown).
    /// Rejected for stale generations, missing/consumed/invalidated/expired state, or when the
    /// current generation already left Pending — a late result for an older generation can never
    /// mutate a newer one.
    /// </summary>
    bool TryTransitionDelivery(long userId, long generation, PasswordResetDeliveryState deliveryState);

    /// <summary>
    /// Atomically increments the failed-attempt count of the identified generation; concurrent
    /// increments are never lost. Reaching <see cref="PasswordResetPolicy.MaxFailedAttempts"/>
    /// failed attempts invalidates the current state. The account itself is never locked.
    /// </summary>
    PasswordResetAttemptResult RecordFailedAttempt(long userId, long generation);

    /// <summary>
    /// Removes the identified current generation after a successful reset. At or after
    /// ExpiresAtUtc the generation is already unusable, so it can never report successful
    /// consumption — it is only lazily removed.
    /// </summary>
    bool TryConsume(long userId, long generation);

    /// <summary>
    /// Removes the identified current generation (e.g. confirm-time eligibility failure);
    /// removed state can never become usable again. At or after ExpiresAtUtc the generation is
    /// already unusable, so it can never report successful invalidation of a usable state —
    /// it is only lazily removed.
    /// </summary>
    bool TryInvalidate(long userId, long generation);
}
using System.Collections.Concurrent;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Infrastructure.Services;

/// <summary>
/// Process-local, per-account synchronized reset-state store for UC-06.
/// State lives only in this instance's memory: a new instance represents a backend
/// restart/redeployment and has no knowledge of prior reset state. Policy values
/// (OTP lifetime, resend cooldown, attempt limit) come from <see cref="PasswordResetPolicy"/>.
/// </summary>
public sealed class InMemoryPasswordResetStateStore : IPasswordResetStateStore
{
    private readonly ConcurrentDictionary<long, AccountEntry> _accounts = new();
    private readonly IDateTimeProvider _dateTimeProvider;

    public InMemoryPasswordResetStateStore(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;
    }

    public PasswordResetState? GetCurrent(long userId)
    {
        var account = GetAccount(userId);
        lock (account.Gate)
        {
            return GetCurrentLocked(account, _dateTimeProvider.UtcNow);
        }
    }

    public PasswordResetIssueResult Issue(long userId, string protectedOtp) =>
        Issue(userId, protectedOtp, _dateTimeProvider.UtcNow);

    public PasswordResetIssueResult Issue(long userId, string protectedOtp, DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedOtp);

        var account = GetAccount(userId);
        lock (account.Gate)
        {
            var now = createdAtUtc;
            if (account.LastIssuedAtUtc is { } lastIssuedAtUtc
                && now - lastIssuedAtUtc < PasswordResetPolicy.ResendCooldown)
            {
                return new PasswordResetIssueResult(
                    PasswordResetIssueOutcome.CooldownSuppressed,
                    GetCurrentLocked(account, now),
                    lastIssuedAtUtc + PasswordResetPolicy.ResendCooldown);
            }

            account.Generation++;
            var state = new PasswordResetState(
                UserId: userId,
                ProtectedOtp: protectedOtp,
                CreatedAtUtc: now,
                ExpiresAtUtc: now + PasswordResetPolicy.OtpTimeToLive,
                FailedAttemptCount: 0,
                DeliveryState: PasswordResetDeliveryState.Pending,
                Generation: account.Generation);
            account.Current = state;
            account.LastIssuedAtUtc = now;

            return new PasswordResetIssueResult(
                PasswordResetIssueOutcome.Issued,
                state,
                CooldownEndsAtUtc: null);
        }
    }

    public bool TryTransitionDelivery(long userId, long generation, PasswordResetDeliveryState deliveryState)
    {
        if (deliveryState == PasswordResetDeliveryState.Pending)
        {
            return false;
        }

        var account = GetAccount(userId);
        lock (account.Gate)
        {
            var state = GetCurrentLocked(account, _dateTimeProvider.UtcNow);
            if (state is null
                || state.Generation != generation
                || state.DeliveryState != PasswordResetDeliveryState.Pending)
            {
                return false;
            }

            account.Current = state with { DeliveryState = deliveryState };
            return true;
        }
    }

    public PasswordResetAttemptResult RecordFailedAttempt(long userId, long generation)
    {
        var account = GetAccount(userId);
        lock (account.Gate)
        {
            var state = GetCurrentLocked(account, _dateTimeProvider.UtcNow);
            if (state is null
                || state.Generation != generation
                || state.FailedAttemptCount >= PasswordResetPolicy.MaxFailedAttempts)
            {
                return PasswordResetAttemptResult.Rejected;
            }

            var failedAttemptCount = state.FailedAttemptCount + 1;
            var invalidated = failedAttemptCount >= PasswordResetPolicy.MaxFailedAttempts;
            account.Current = invalidated
                ? null
                : state with { FailedAttemptCount = failedAttemptCount };

            return new PasswordResetAttemptResult(true, failedAttemptCount, invalidated);
        }
    }

    public bool TryConsume(long userId, long generation)
    {
        return RemoveCurrentGeneration(userId, generation);
    }

    public bool TryInvalidate(long userId, long generation)
    {
        return RemoveCurrentGeneration(userId, generation);
    }

    /// <summary>
    /// Expiry-aware removal: an expired generation is already unusable, so it can never
    /// report successful consumption/invalidation — it is only lazily cleaned up.
    /// </summary>
    private bool RemoveCurrentGeneration(long userId, long generation)
    {
        var account = GetAccount(userId);
        lock (account.Gate)
        {
            var state = GetCurrentLocked(account, _dateTimeProvider.UtcNow);
            if (state is null || state.Generation != generation)
            {
                return false;
            }

            account.Current = null;
            return true;
        }
    }

    /// <summary>
    /// Must be called while holding <paramref name="account"/>'s gate. Expired state is
    /// lazily removed and treated as absent so it can never become usable again.
    /// </summary>
    private static PasswordResetState? GetCurrentLocked(AccountEntry account, DateTimeOffset now)
    {
        var state = account.Current;
        if (state is null)
        {
            return null;
        }

        if (now >= state.ExpiresAtUtc)
        {
            account.Current = null;
            return null;
        }

        return state;
    }

    private AccountEntry GetAccount(long userId)
    {
        return _accounts.GetOrAdd(userId, static _ => new AccountEntry());
    }

    private sealed class AccountEntry
    {
        public object Gate { get; } = new();

        public long Generation { get; set; }

        public DateTimeOffset? LastIssuedAtUtc { get; set; }

        public PasswordResetState? Current { get; set; }
    }
}
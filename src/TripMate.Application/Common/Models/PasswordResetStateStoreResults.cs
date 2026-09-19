namespace TripMate.Application.Common.Models;

public enum PasswordResetIssueOutcome
{
    /// <summary>A new reset generation was created and supersedes any previous generation.</summary>
    Issued,

    /// <summary>The resend cooldown is active; no new generation was created.</summary>
    CooldownSuppressed,
}

public sealed record PasswordResetIssueResult(
    PasswordResetIssueOutcome Outcome,
    PasswordResetState? State,
    DateTimeOffset? CooldownEndsAtUtc);

public sealed record PasswordResetAttemptResult(
    bool Accepted,
    int FailedAttemptCount,
    bool Invalidated)
{
    public static PasswordResetAttemptResult Rejected { get; } = new(false, 0, false);
}
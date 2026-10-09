using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TripReviewMediaRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
    [Fact]
    public void UnknownAbsenceThenLateSuccessRequiresCurrentCleanupFence()
    {
        var r = TripReviewMediaRecovery.Create(Guid.NewGuid()); var upload = Guid.NewGuid();
        r.TryClaimUpload(upload, Now).Should().BeTrue();
        r.UploadOutcome.Should().Be(TripReviewMediaRecovery.Unknown);
        r.TryClaimCleanup(Guid.NewGuid(), Now.AddSeconds(119)).Should().BeFalse();
        var cleanup = Guid.NewGuid(); r.TryClaimCleanup(cleanup, Now.AddMinutes(3)).Should().BeTrue();
        r.CompleteCleanup(cleanup, Now.AddMinutes(3), true).Should().BeFalse();
        r.TryObserveUpload(upload, true).Should().BeTrue();
        r.CompleteCleanup(Guid.NewGuid(), Now.AddMinutes(3), true).Should().BeFalse();
        r.CompleteCleanup(cleanup, Now.AddMinutes(3), false).Should().BeFalse();
        r.CompleteCleanup(cleanup, Now.AddMinutes(3), true).Should().BeTrue();
    }
    [Fact]
    public void UnknownAbsenceExhaustsWithoutTerminalEvidence()
    {
        var r = TripReviewMediaRecovery.Create(Guid.NewGuid()); r.TryClaimUpload(Guid.NewGuid(), Now);
        var now = Now.AddMinutes(3);
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var fence = Guid.NewGuid(); r.TryClaimCleanup(fence, now).Should().BeTrue();
            r.Attempts.Should().Be(attempt); r.CompleteCleanup(fence, now, true).Should().BeFalse();
            r.RecordFailure(fence, now, TripReviewMediaRecovery.OutcomeUnknown).Should().BeTrue();
            now = r.NextAttemptAtUtc ?? now.AddDays(1);
        }
        r.Exhausted.Should().BeTrue(); r.HasTerminalUploadEvidence.Should().BeFalse();
        r.NextAttemptAtUtc.Should().BeNull(); r.TryClaimCleanup(Guid.NewGuid(), now).Should().BeFalse();
    }
    [Fact]
    public void KnownRejectedAndConfirmedAbsenceCanComplete()
    {
        var r = TripReviewMediaRecovery.Create(Guid.NewGuid()); var upload = Guid.NewGuid();
        r.TryClaimUpload(upload, Now); r.TryObserveUpload(upload, false).Should().BeTrue();
        var cleanup = Guid.NewGuid(); r.TryClaimCleanup(cleanup, Now).Should().BeTrue();
        r.CompleteCleanup(cleanup, Now, true).Should().BeTrue();
    }
    [Fact]
    public void DispatchFenceIsSingleUseAndLateEvidenceCannotBeReversed()
    {
        var r = TripReviewMediaRecovery.Create(Guid.NewGuid()); var upload = Guid.NewGuid();
        r.TryClaimUpload(upload, Now).Should().BeTrue();
        r.TryClaimUpload(Guid.NewGuid(), Now.AddHours(2)).Should().BeFalse();
        r.TryObserveUpload(Guid.NewGuid(), true).Should().BeFalse();
        r.TryObserveUpload(upload, true).Should().BeTrue();
        r.TryObserveUpload(upload, true).Should().BeTrue();
        r.TryObserveUpload(upload, false).Should().BeFalse();
    }
    [Fact]
    public void CrashedCleanupClaimsConsumeBudgetAndExpireIntoExhaustion()
    {
        var r = TripReviewMediaRecovery.Create(Guid.NewGuid());
        for (var i = 0; i < 8; i++) r.TryClaimCleanup(Guid.NewGuid(), Now.AddMinutes(i * 3)).Should().BeTrue();
        r.TryClaimCleanup(Guid.NewGuid(), Now.AddDays(1)).Should().BeFalse();
        r.Exhausted.Should().BeTrue(); r.Attempts.Should().Be(8);
    }
    [Fact]
    public void RetryBackoffIsBoundedAndStaleOwnerCannotMutate()
    {
        var r = TripReviewMediaRecovery.Create(Guid.NewGuid()); var fence = Guid.NewGuid();
        r.TryClaimCleanup(fence, Now);
        r.RecordFailure(Guid.NewGuid(), Now, TripReviewMediaRecovery.Transient).Should().BeFalse();
        r.RecordFailure(fence, Now, TripReviewMediaRecovery.Transient).Should().BeTrue();
        r.NextAttemptAtUtc.Should().Be(Now.AddMinutes(1));
        r.TryClaimCleanup(Guid.NewGuid(), Now.AddSeconds(59)).Should().BeFalse();
        var second = Guid.NewGuid(); r.TryClaimCleanup(second, Now.AddMinutes(1)).Should().BeTrue();
        r.RecordFailure(second, Now.AddMinutes(1), TripReviewMediaRecovery.Transient).Should().BeTrue();
        r.NextAttemptAtUtc.Should().Be(Now.AddMinutes(3));
    }
    [Fact]
    public void NeverDispatchedNeedsPositiveResolutionAndPermanentFailureRequiresManualRecovery()
    {
        var r = TripReviewMediaRecovery.Create(Guid.NewGuid()); var fence = Guid.NewGuid();
        r.HasTerminalUploadEvidence.Should().BeTrue(); r.TryClaimCleanup(fence, Now);
        r.CompleteCleanup(fence, Now, false).Should().BeFalse();
        r.RecordFailure(fence, Now, TripReviewMediaRecovery.Permanent, true).Should().BeTrue();
        r.Exhausted.Should().BeTrue(); r.LastFailureCode.Should().Be(TripReviewMediaRecovery.Permanent);
    }
}
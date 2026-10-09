using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Reviews.Media;

using static TripMate.Api.IntegrationTests.Reviews.TripReviewMediaSqlServerTests;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class TripReviewMediaRecoverySqlTests
{
    [SqlServerFact]
    public async Task UnknownAbsence_LateSuccess_ThenSafeCleanup()
    {
        await using var database = await Database(); await using var db = database.CreateDbContext();
        var op = Operation(); var journal = new SqlServerReviewMediaJournal(db);
        (await journal.ReserveAsync(1, 1, [op], default)).IsSuccess.Should().BeTrue();
        var upload = (await journal.ClaimUploadAsync(op.Id, 1, Now, default)).Value;
        await journal.ReconcileAbandonedAsync(Now.AddMinutes(6), 20, default);
        var cleanup = (await journal.ClaimCleanupAsync(Now.AddMinutes(6), 20, default)).Single();
        await journal.CompleteCleanupAsync(cleanup, ReviewMediaStorageOutcome.AlreadyAbsent, false, Now.AddMinutes(6), default);
        await using (var read = database.CreateDbContext())
        {
            var row = await read.TripReviewMediaOperations.SingleAsync(); row.State.Should().Be("CleanupPending"); row.CleanedAtUtc.Should().BeNull();
        }
        (await journal.CompleteUploadAsync(upload, 1, new(ReviewMediaStorageOutcome.Success, "https://images.example.invalid/a", 1), Now.AddMinutes(7), default)).IsFailure.Should().BeTrue();
        cleanup = (await journal.ClaimCleanupAsync(Now.AddMinutes(8), 20, default)).Single();
        await journal.CompleteCleanupAsync(cleanup, ReviewMediaStorageOutcome.AlreadyAbsent, true, Now.AddMinutes(8), default);
        await using var final = database.CreateDbContext();
        (await final.TripReviewMediaOperations.SingleAsync()).State.Should().Be("Cleaned");
    }

    [SqlServerFact]
    public async Task RepeatedUnknownAbsence_ExhaustsWithoutCleaned()
    {
        await using var database = await Database(); await using var db = database.CreateDbContext();
        var op = Operation(); var journal = new SqlServerReviewMediaJournal(db); await journal.ReserveAsync(1, 1, [op], default);
        await journal.ClaimUploadAsync(op.Id, 1, Now, default); await journal.ReconcileAbandonedAsync(Now.AddMinutes(6), 20, default);
        for (int i = 0; i < 8; i++) { var time = Now.AddDays(i + 1); var claim = (await journal.ClaimCleanupAsync(time, 20, default)).Single(); await journal.CompleteCleanupAsync(claim, ReviewMediaStorageOutcome.AlreadyAbsent, false, time, default); }
        (await journal.ClaimCleanupAsync(Now.AddDays(10), 20, default)).Should().BeEmpty();
        await using var read = database.CreateDbContext();
        (await read.TripReviewMediaOperations.SingleAsync()).State.Should().Be("CleanupPending");
        var recovery = await read.Set<TripReviewMediaRecovery>().SingleAsync();
        recovery.UploadOutcome.Should().Be(TripReviewMediaRecovery.Unknown);
        recovery.Attempts.Should().Be(8);
        recovery.Exhausted.Should().BeTrue();
        recovery.NextAttemptAtUtc.Should().BeNull();
        recovery.LastFailureCode.Should().Be(TripReviewMediaRecovery.OutcomeUnknown);
        recovery.LastFailureAtUtc.Should().NotBeNull();
    }

    [SqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownTerminalOrNeverDispatched_AbsenceCanClean(bool dispatched)
    {
        await using var database = await Database(); await using var db = database.CreateDbContext();
        var op = Operation(); var journal = new SqlServerReviewMediaJournal(db); await journal.ReserveAsync(1, 1, [op], default);
        if (dispatched) { var upload = (await journal.ClaimUploadAsync(op.Id, 1, Now, default)).Value; await journal.CompleteUploadAsync(upload, 1, new(ReviewMediaStorageOutcome.PermanentFailure), Now, default); }
        await journal.RequestCleanupAsync(op.BatchId, 1, 1, Now, default);
        var cleanup = (await journal.ClaimCleanupAsync(Now.AddMinutes(3), 20, default)).Single();
        await journal.CompleteCleanupAsync(cleanup, ReviewMediaStorageOutcome.AlreadyAbsent, false, Now.AddMinutes(3), default);
        await using var read = database.CreateDbContext(); (await read.TripReviewMediaOperations.SingleAsync()).State.Should().Be("Cleaned");
    }

    [SqlServerFact]
    public async Task ExpiredUploadCannotBecomeUploaded_OrDispatchAgain()
    {
        await using var database = await Database(); await using var db = database.CreateDbContext();
        var op = Operation(); var journal = new SqlServerReviewMediaJournal(db); await journal.ReserveAsync(1, 1, [op], default);
        var upload = (await journal.ClaimUploadAsync(op.Id, 1, Now, default)).Value;
        (await journal.ClaimUploadAsync(op.Id, 1, Now.AddMinutes(4), default)).IsFailure.Should().BeTrue();
        (await journal.CompleteUploadAsync(upload, 1, new(ReviewMediaStorageOutcome.Success, "https://images.example.invalid/a", 1), Now.AddMinutes(4), default)).IsFailure.Should().BeTrue();
        await using var read = database.CreateDbContext();
        (await read.TripReviewMediaOperations.SingleAsync()).State.Should().NotBe("Uploaded");
        var recovery = await read.Set<TripReviewMediaRecovery>().SingleAsync();
        recovery.UploadFence.Should().Be(upload.Token);
        recovery.UploadLeaseUntilUtc.Should().Be(Now.AddMinutes(2));
        recovery.UploadOutcome.Should().Be(TripReviewMediaRecovery.Unknown);
    }

    [SqlServerFact]
    public async Task DelayedUploadClaim_RestartsAbandonmentGraceFromLatestActivity()
    {
        await using var database = await Database(); await using var db = database.CreateDbContext();
        var op = Operation(); var journal = new SqlServerReviewMediaJournal(db);
        (await journal.ReserveAsync(1, 1, [op], default)).IsSuccess.Should().BeTrue();

        (await journal.ClaimUploadAsync(op.Id, 1, Now.AddMinutes(4), default)).IsSuccess.Should().BeTrue();
        await journal.ReconcileAbandonedAsync(Now.AddMinutes(7), 20, default);

        await using (var stillActive = database.CreateDbContext())
            (await stillActive.TripReviewMediaOperations.SingleAsync()).State.Should().Be(TripReviewMediaOperation.Reserved);

        await journal.ReconcileAbandonedAsync(Now.AddMinutes(10), 20, default);
        await using var abandoned = database.CreateDbContext();
        (await abandoned.TripReviewMediaOperations.SingleAsync()).State.Should().Be(TripReviewMediaOperation.CleanupPending);
    }

    [SqlServerFact]
    public async Task AcceptedUploadCompletion_ReplayWithSameFenceAndMetadataIsIdempotent()
    {
        await using var database = await Database(); await using var db = database.CreateDbContext();
        var op = Operation(); var journal = new SqlServerReviewMediaJournal(db);
        await journal.ReserveAsync(1, 1, [op], default);
        var claim = (await journal.ClaimUploadAsync(op.Id, 1, Now, default)).Value;
        var result = new ReviewMediaStorageResult(ReviewMediaStorageOutcome.Success, "https://images.example.invalid/a", 1);
        var first = await journal.CompleteUploadAsync(claim, 1, result, Now, default);
        first.IsSuccess.Should().BeTrue();

        var replay = await journal.CompleteUploadAsync(claim, 1, result, Now.AddMinutes(4), default);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Version.Should().Equal(first.Value.Version);
        await using var read = database.CreateDbContext();
        (await read.TripReviewMediaOperations.SingleAsync()).State.Should().Be(TripReviewMediaOperation.Uploaded);
    }

    [SqlServerFact]
    public async Task CleanupFenceReplacement_RejectsStaleCompletion()
    {
        await using var database = await Database(); await using var db = database.CreateDbContext();
        var op = Operation(); var journal = new SqlServerReviewMediaJournal(db); await journal.ReserveAsync(1, 1, [op], default); await journal.RequestCleanupAsync(op.BatchId, 1, 1, Now, default);
        var old = (await journal.ClaimCleanupAsync(Now, 20, default)).Single();
        var current = (await journal.ClaimCleanupAsync(Now.AddMinutes(3), 20, default)).Single(); current.Token.Should().NotBe(old.Token);
        (await journal.CompleteCleanupAsync(old, ReviewMediaStorageOutcome.Success, true, Now.AddMinutes(3), default)).IsFailure.Should().BeTrue();
        await using var read = database.CreateDbContext();
        (await read.TripReviewMediaOperations.SingleAsync()).State.Should().Be("CleanupPending");
        var recovery = await read.Set<TripReviewMediaRecovery>().SingleAsync();
        recovery.CleanupFence.Should().Be(current.Token);
        recovery.CleanupLeaseUntilUtc.Should().Be(Now.AddMinutes(5));
        recovery.Attempts.Should().Be(2);
    }

    [SqlServerFact]
    public async Task ActiveUploadInEarlierBatch_DoesNotStarveLaterEligibleCleanup()
    {
        await using var database = await Database(); await using var db = database.CreateDbContext();
        var journal = new SqlServerReviewMediaJournal(db);
        var blockedBatch = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var blocked = new[]
        {
            TripReviewMediaOperation.Reserve(Guid.Parse("00000000-0000-0000-0000-000000000001"),blockedBatch,1,1,0,Guid.NewGuid().ToString("N"),"image/png",".png",1,1,1,Now),
            TripReviewMediaOperation.Reserve(Guid.Parse("00000000-0000-0000-0000-000000000002"),blockedBatch,1,1,1,Guid.NewGuid().ToString("N"),"image/png",".png",1,1,1,Now)
        };
        (await journal.ReserveAsync(1, 1, blocked, default)).IsSuccess.Should().BeTrue();
        (await journal.ClaimUploadAsync(blocked[1].Id, 1, Now, default)).IsSuccess.Should().BeTrue();
        (await journal.RequestCleanupAsync(blockedBatch, 1, 1, Now, default)).IsSuccess.Should().BeTrue();

        var eligibleBatch = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff0");
        var eligible = TripReviewMediaOperation.Reserve(Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff1"), eligibleBatch, 1, 1, 0, Guid.NewGuid().ToString("N"), "image/png", ".png", 1, 1, 1, Now);
        (await journal.ReserveAsync(1, 1, [eligible], default)).IsSuccess.Should().BeTrue();
        (await journal.RequestCleanupAsync(eligibleBatch, 1, 1, Now, default)).IsSuccess.Should().BeTrue();

        var claimed = await journal.ClaimCleanupAsync(Now.AddMinutes(1), 1, default);
        claimed.Should().ContainSingle().Which.OperationId.Should().Be(eligible.Id);
    }

    [SqlServerFact]
    public async Task AdoptedOperation_IsNeverReconciledOrClaimedForCleanup()
    {
        await using var database = await Database(); await using var db = database.CreateDbContext();
        var op = Operation(); var journal = new SqlServerReviewMediaJournal(db);
        var reserved = await journal.ReserveAsync(1, 1, [op], default);
        var claim = (await journal.ClaimUploadAsync(op.Id, 1, Now, default)).Value;
        var uploaded = await journal.CompleteUploadAsync(claim, 1,
            new(ReviewMediaStorageOutcome.Success, "https://images.example.invalid/a", 1), Now, default);
        uploaded.IsSuccess.Should().BeTrue();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            (await journal.AdoptAsync(op.BatchId, 1, [uploaded.Value], Parent(), Now, default)).IsSuccess.Should().BeTrue();
            await tx.CommitAsync();
        }

        await journal.ReconcileAbandonedAsync(Now.AddDays(1), 20, default);
        (await journal.ClaimCleanupAsync(Now.AddDays(1), 20, default)).Should().BeEmpty();
        await using var read = database.CreateDbContext();
        (await read.TripReviewMediaOperations.SingleAsync()).State.Should().Be(TripReviewMediaOperation.Adopted);
        (await read.TripReviewMedia.SingleAsync()).OperationId.Should().Be(op.Id);
    }
}
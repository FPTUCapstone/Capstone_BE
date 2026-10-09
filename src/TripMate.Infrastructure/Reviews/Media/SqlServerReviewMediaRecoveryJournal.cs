using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Reviews.Media;

public sealed partial class SqlServerReviewMediaJournal
{
    private async Task<TripReviewMediaRecovery?> Recovery(Guid id, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<TripReviewMediaRecovery>().SingleOrDefault(x => x.Entity.OperationId == id);
        if (tracked != null) tracked.State = EntityState.Detached;
        return await db.Set<TripReviewMediaRecovery>().FromSqlInterpolated($"SELECT * FROM social.TripReviewMediaRecovery WITH(UPDLOCK,HOLDLOCK) WHERE operation_id={id}").SingleOrDefaultAsync(ct);
    }
    private async Task<TripReviewMediaOperation?> LockRecoveryOperation(Guid id, CancellationToken ct)
    {
        var batch = await db.TripReviewMediaOperations.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.BatchId).SingleOrDefaultAsync(ct);
        if (batch == null) return null;
        await BatchLock(batch.Value, ct); return await LockedOperation(id, ct);
    }
    public async Task<Result<ReviewMediaUploadClaim>> ClaimUploadAsync(Guid id, long owner, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var op = await LockRecoveryOperation(id, ct);
        if (op == null || op.TravelerUserId != owner || op.State != TripReviewMediaOperation.Reserved) return Fail<ReviewMediaUploadClaim>(ReviewMediaErrors.InvalidState);
        var recovery = await Recovery(id, ct); var token = Guid.NewGuid();
        if (recovery == null || !recovery.TryClaimUpload(token, now) || !op.TryRecordUploadDispatch(now)) return Fail<ReviewMediaUploadClaim>(ReviewMediaErrors.InvalidState);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Result.Success(new ReviewMediaUploadClaim(id, token, op.PublicId));
    }
    public async Task<Result<ReviewMediaVersion>> CompleteUploadAsync(ReviewMediaUploadClaim claim, long owner, ReviewMediaStorageResult result, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var op = await LockRecoveryOperation(claim.OperationId, ct);
        if (op == null || op.TravelerUserId != owner || op.PublicId != claim.PublicId || op.State is TripReviewMediaOperation.Adopted or TripReviewMediaOperation.Cleaned) return Fail<ReviewMediaVersion>(ReviewMediaErrors.InvalidState);
        var recovery = await Recovery(op.Id, ct);
        if (recovery == null || recovery.UploadFence != claim.Token) return Fail<ReviewMediaVersion>(ReviewMediaErrors.StaleVersion);
        if (op.State == TripReviewMediaOperation.Uploaded)
        {
            bool same = result.Outcome == ReviewMediaStorageOutcome.Success &&
                result.DeliveryUrl == op.DeliveryUrl && result.StoredByteLength == op.StoredByteLength &&
                recovery.UploadOutcome == TripReviewMediaRecovery.Succeeded;
            return same ? Result.Success(Version(op)) : Fail<ReviewMediaVersion>(ReviewMediaErrors.MetadataConflict);
        }
        var current = recovery.UploadLeaseUntilUtc > now;
        bool success = result.Outcome == ReviewMediaStorageOutcome.Success && result.DeliveryUrl != null && result.StoredByteLength.HasValue && TripReviewMediaOperation.ValidUpload(result.DeliveryUrl, result.StoredByteLength.Value);
        if (current && (success || result.Outcome == ReviewMediaStorageOutcome.PermanentFailure))
            recovery.TryObserveUpload(claim.Token, success);
        var accepted = success && current && op.State == TripReviewMediaOperation.Reserved && op.TryRecordUpload(result.DeliveryUrl!, result.StoredByteLength!.Value, now);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return accepted ? Result.Success(Version(op)) : Fail<ReviewMediaVersion>(ReviewMediaErrors.InvalidState);
    }
    public Task<Result> RequestCleanupAsync(Guid batch, long booking, long owner,
        DateTimeOffset now, CancellationToken ct) =>
        RequestCleanupAsync(batch, ReviewableRecordRef.CommerceBooking(booking), owner, now, ct);

    public async Task<Result> RequestCleanupAsync(Guid batch, ReviewableRecordRef reviewableRecord,
        long owner, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.TripReviewMediaOperations.AsNoTracking().Where(x => x.BatchId == batch).ToListAsync(ct);
        return await MarkCleanupPendingAsync(
            batch, reviewableRecord, owner, rows.Select(Version).ToArray(), now, ct);
    }
    public async Task ReconcileAbandonedAsync(DateTimeOffset now, int maximum, CancellationToken ct)
    {
        var cutoff = now.AddMinutes(-5);
        var batches = await db.TripReviewMediaOperations.AsNoTracking().Where(x => x.State == TripReviewMediaOperation.Reserved || x.State == TripReviewMediaOperation.Uploaded)
            .GroupBy(x => x.BatchId).Where(x => x.Max(r => r.UpdatedAtUtc) <= cutoff).OrderBy(x => x.Key).Select(x => x.Key).Take(Math.Clamp(maximum, 1, 20)).ToListAsync(ct);
        foreach (var batch in batches)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await LockedBatch(batch, ct);
            bool eligible = rows.Count > 0 && rows.All(x => (x.State == TripReviewMediaOperation.Reserved || x.State == TripReviewMediaOperation.Uploaded) && x.UpdatedAtUtc <= cutoff);
            foreach (var row in rows) { var rec = await Recovery(row.Id, ct); if (rec == null || rec.UploadLeaseUntilUtc > now) eligible = false; }
            if (eligible)
            {
                MarkCompleteBatchCleanupPending(rows, now);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }
    public async Task<IReadOnlyList<ReviewMediaCleanupClaim>> ClaimCleanupAsync(DateTimeOffset now, int maximum, CancellationToken ct)
    {
        var ids = await (from op in db.TripReviewMediaOperations.AsNoTracking()
                         join r in db.Set<TripReviewMediaRecovery>().AsNoTracking() on op.Id equals r.OperationId
                         where op.State == TripReviewMediaOperation.CleanupPending && !r.Exhausted && (r.NextAttemptAtUtc == null || r.NextAttemptAtUtc <= now)
                         && (r.CleanupLeaseUntilUtc == null || r.CleanupLeaseUntilUtc <= now) && (r.UploadLeaseUntilUtc == null || r.UploadLeaseUntilUtc <= now)
                         && !(from sibling in db.TripReviewMediaOperations.AsNoTracking()
                              join siblingRecovery in db.Set<TripReviewMediaRecovery>().AsNoTracking() on sibling.Id equals siblingRecovery.OperationId
                              where sibling.BatchId == op.BatchId &&
                                  ((sibling.State != TripReviewMediaOperation.CleanupPending && sibling.State != TripReviewMediaOperation.Cleaned) || siblingRecovery.UploadLeaseUntilUtc > now)
                              select sibling.Id).Any()
                         orderby op.Id
                         select op.Id).Take(Math.Clamp(maximum, 1, 20)).ToListAsync(ct);
        var claims = new List<ReviewMediaCleanupClaim>();
        foreach (var id in ids)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct); var op = await LockRecoveryOperation(id, ct);
            if (op == null || op.State != TripReviewMediaOperation.CleanupPending) continue;
            var rows = await LockedBatch(op.BatchId, ct);
            bool valid = rows.All(x => x.State is TripReviewMediaOperation.CleanupPending or TripReviewMediaOperation.Cleaned);
            foreach (var row in rows) { var metadata = await Recovery(row.Id, ct); if (metadata == null || metadata.UploadLeaseUntilUtc > now) valid = false; }
            if (!valid) continue;
            var recovery = await Recovery(id, ct); var token = Guid.NewGuid();
            if (recovery != null && recovery.TryClaimCleanup(token, now)) claims.Add(new(id, token, op.PublicId, recovery.HasTerminalUploadEvidence));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        return claims;
    }
    public async Task<Result> CompleteCleanupAsync(ReviewMediaCleanupClaim claim, ReviewMediaStorageOutcome outcome, bool terminalEvidence, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var op = await LockRecoveryOperation(claim.OperationId, ct);
        if (op == null || op.State != TripReviewMediaOperation.CleanupPending || op.PublicId != claim.PublicId) return Fail(ReviewMediaErrors.InvalidState);
        var recovery = await Recovery(op.Id, ct);
        if (recovery == null || !recovery.OwnsCleanup(claim.Token, now) || recovery.UploadLeaseUntilUtc > now) return Fail(ReviewMediaErrors.StaleVersion);
        if (terminalEvidence) recovery.TryObserveProviderSuccess(claim.Token, now);
        bool resolved = outcome is ReviewMediaStorageOutcome.Success or ReviewMediaStorageOutcome.AlreadyAbsent;
        if (recovery.CompleteCleanup(claim.Token, now, resolved))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE social.TripReviewMediaOperations SET state={TripReviewMediaOperation.Cleaned},cleaned_at={now.UtcDateTime},updated_at={now.UtcDateTime} WHERE operation_id={op.Id} AND state={TripReviewMediaOperation.CleanupPending}", ct);
            db.Entry(op).State = EntityState.Detached;
        }
        else recovery.RecordFailure(claim.Token, now, outcome == ReviewMediaStorageOutcome.PermanentFailure ? TripReviewMediaRecovery.Permanent : resolved ? TripReviewMediaRecovery.OutcomeUnknown : TripReviewMediaRecovery.Transient, outcome == ReviewMediaStorageOutcome.PermanentFailure);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Result.Success();
    }
}
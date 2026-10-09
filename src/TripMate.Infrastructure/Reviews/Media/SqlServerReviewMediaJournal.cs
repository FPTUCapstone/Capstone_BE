using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.Reviews.Media;

public sealed partial class SqlServerReviewMediaJournal(ApplicationDbContext db) : IReviewMediaJournal, IReviewMediaRecoveryJournal
{
    private static Result Fail(string code) => Result.Failure(code, "Review media operation rejected.");
    private static Result<T> Fail<T>(string code) => Result.Failure<T>(code, "Review media operation rejected.");
    private static ReviewMediaVersion Version(TripReviewMediaOperation op) => new(op.Id, op.Version.ToArray());
    private Task<bool> Owns(long booking, long traveler, CancellationToken ct) =>
        Owns(ReviewableRecordRef.CommerceBooking(booking), traveler, ct);

    private async Task<bool> Owns(ReviewableRecordRef reviewableRecord, long traveler, CancellationToken ct) =>
        reviewableRecord.IsCommerceBooking
            ? await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM commerce.Bookings WHERE booking_id={reviewableRecord.Id} AND traveler_user_id={traveler}").SingleAsync(ct) == 1
            : await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM commercial.ServiceBookings WHERE service_booking_id={reviewableRecord.Id} AND traveler_user_id={traveler}").SingleAsync(ct) == 1;

    public Task<Result<IReadOnlyList<ReviewMediaVersion>>> ReserveAsync(long bookingId, long travelerId,
        IReadOnlyList<TripReviewMediaOperation> operations, CancellationToken ct) =>
        ReserveAsync(ReviewableRecordRef.CommerceBooking(bookingId), travelerId, operations, ct);

    public async Task<Result<IReadOnlyList<ReviewMediaVersion>>> ReserveAsync(
        ReviewableRecordRef reviewableRecord, long travelerId,
        IReadOnlyList<TripReviewMediaOperation> operations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        if (db.Database.CurrentTransaction != null) return Fail<IReadOnlyList<ReviewMediaVersion>>(ReviewMediaErrors.InvalidState);
        if (operations.Count is < 1 or > 5 || operations.Select(x => x.BatchId).Distinct().Count() != 1 ||
            operations.Select(x => x.Id).Distinct().Count() != operations.Count ||
            !operations.OrderBy(x => x.SortOrder).Select(x => (int)x.SortOrder).SequenceEqual(Enumerable.Range(0, operations.Count)) ||
            operations.Any(x => !Matches(x, reviewableRecord)
                || x.TravelerUserId != travelerId || x.State != TripReviewMediaOperation.Reserved))
            return Fail<IReadOnlyList<ReviewMediaVersion>>(ReviewMediaErrors.MetadataConflict);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (!await Owns(reviewableRecord, travelerId, ct)) return Fail<IReadOnlyList<ReviewMediaVersion>>(ReviewMediaErrors.MissingOrForeign);
            // Serialize reservation for an already known server batch (also protects exact membership).
            var batch = operations[0].BatchId;
            await BatchLock(batch, ct);
            if (await db.TripReviewMediaOperations.AnyAsync(x => x.BatchId == batch, ct)) return Fail<IReadOnlyList<ReviewMediaVersion>>(ReviewMediaErrors.InvalidState);
            db.TripReviewMediaOperations.AddRange(operations);
            db.Set<TripReviewMediaRecovery>().AddRange(operations.Select(x => TripReviewMediaRecovery.Create(x.Id)));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success<IReadOnlyList<ReviewMediaVersion>>(operations.Select(Version).ToArray());
        }
        catch { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    }

    public async Task<Result> AdoptAsync(Guid batchId, long travelerId, IReadOnlyList<ReviewMediaVersion> expected, TripReview parent, DateTimeOffset now, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction;
        if (tx == null) return Fail(ReviewMediaErrors.TransactionRequired);
        async Task<Result> Reject(string code)
        { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); return Fail(code); }
        try
        {
            var rows = await LockedBatch(batchId, ct);
            var reviewableRecord = ParentIdentity(parent);
            if (!Exact(rows, expected)
                || reviewableRecord is null
                || rows.Any(x => x.TravelerUserId != travelerId || !Matches(x, reviewableRecord))
                || parent.TravelerUserId != travelerId
                || !await Owns(reviewableRecord, travelerId, ct))
                return await Reject(ReviewMediaErrors.MissingOrForeign);
            if (parent.PublicationStatus != TripReview.PublishedStatus) return await Reject(ReviewMediaErrors.InvalidState);
            var ids = rows.Select(x => x.Id).ToArray();
            var links = await db.TripReviewMedia.AsNoTracking().Where(x => ids.Contains(x.OperationId)).ToListAsync(ct);
            if (rows.All(x => x.State == TripReviewMediaOperation.Adopted))
            {
                var allParentLinks = await db.TripReviewMedia.CountAsync(x => x.TripReviewId == parent.Id, ct);
                return parent.Id > 0 && links.Count == rows.Count && allParentLinks == rows.Count && links.All(x => x.TripReviewId == parent.Id)
                    ? Result.Success() : await Reject(ReviewMediaErrors.InvalidState);
            }
            if (rows.Any(x => x.State != TripReviewMediaOperation.Uploaded) || links.Count != 0) return await Reject(ReviewMediaErrors.InvalidState);
            if (!VersionsMatch(rows, expected)) return await Reject(ReviewMediaErrors.StaleVersion);
            if (parent.Id > 0 && await db.TripReviewMedia.AnyAsync(x => x.TripReviewId == parent.Id, ct)) return await Reject(ReviewMediaErrors.InvalidState);
            if (db.Entry(parent).State == EntityState.Detached)
            {
                if (parent.Id != 0) return await Reject(ReviewMediaErrors.InvalidState);
                db.Add(parent);
            }
            foreach (var row in rows) { row.TryAdopt(now); db.TripReviewMedia.Add(TripReviewMedia.Link(parent, row, now)); }
            await db.SaveChangesAsync(ct);
            return Result.Success(); // Caller alone commits publication.
        }
        catch { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    }

    public Task<Result> MarkCleanupPendingAsync(Guid batchId, long bookingId, long travelerId,
        IReadOnlyList<ReviewMediaVersion> expected, DateTimeOffset now, CancellationToken ct) =>
        MarkCleanupPendingAsync(batchId, ReviewableRecordRef.CommerceBooking(bookingId),
            travelerId, expected, now, ct);

    public async Task<Result> MarkCleanupPendingAsync(Guid batchId,
        ReviewableRecordRef reviewableRecord, long travelerId,
        IReadOnlyList<ReviewMediaVersion> expected, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        if (db.Database.CurrentTransaction != null) return Fail(ReviewMediaErrors.InvalidState);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var rows = await LockedBatch(batchId, ct);
            if (!Exact(rows, expected)
                || rows.Any(x => !Matches(x, reviewableRecord) || x.TravelerUserId != travelerId)
                || !await Owns(reviewableRecord, travelerId, ct))
                return Fail(ReviewMediaErrors.MissingOrForeign);
            if (rows.All(x => x.State == TripReviewMediaOperation.CleanupPending)) return Result.Success();
            if (rows.Any(x => x.State != TripReviewMediaOperation.Reserved && x.State != TripReviewMediaOperation.Uploaded)) return Fail(ReviewMediaErrors.InvalidState);
            if (!VersionsMatch(rows, expected)) return Fail(ReviewMediaErrors.StaleVersion);
            MarkCompleteBatchCleanupPending(rows, now);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Result.Success();
        }
        catch { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    }

    private static bool Exact(List<TripReviewMediaOperation> rows, IReadOnlyList<ReviewMediaVersion> expected) => rows.Count is >= 1 and <= 5 &&
        rows.Count == expected.Count && expected.Select(x => x.OperationId).Distinct().Count() == rows.Count && rows.All(x => expected.Any(e => e.OperationId == x.Id));
    private static bool VersionsMatch(List<TripReviewMediaOperation> rows, IReadOnlyList<ReviewMediaVersion> expected) => rows.All(x => x.Version.SequenceEqual(expected.Single(e => e.OperationId == x.Id).Version));

    private static bool Matches(TripReviewMediaOperation operation, ReviewableRecordRef reviewableRecord) =>
        reviewableRecord.IsCommerceBooking
            ? operation.BookingId == reviewableRecord.Id && operation.ServiceBookingId is null
            : operation.ServiceBookingId == reviewableRecord.Id && operation.BookingId is null;

    private static ReviewableRecordRef? ParentIdentity(TripReview parent) =>
        parent.BookingId is > 0 && parent.ServiceBookingId is null
            ? ReviewableRecordRef.CommerceBooking(parent.BookingId.Value)
            : parent.ServiceBookingId is > 0 && parent.BookingId is null
                ? ReviewableRecordRef.ServiceBooking(parent.ServiceBookingId.Value)
                : null;

    // Shared complete-batch transition; callers hold the same batch/ordered row locks.
    private static void MarkCompleteBatchCleanupPending(List<TripReviewMediaOperation> rows, DateTimeOffset now)
    {
        foreach (var row in rows) row.TryMarkCleanupPending(now);
    }

    private Task BatchLock(Guid batch, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
        DECLARE @result int;
        EXEC @result=sys.sp_getapplock @Resource={"TripReviewMedia:" + batch.ToString("N")},@LockMode='Exclusive',@LockOwner='Transaction';
        IF @result<0 THROW 51000,'Cannot lock review media batch.',1;
        """, ct);
    private async Task<List<TripReviewMediaOperation>> LockedBatch(Guid batch, CancellationToken ct)
    {
        await BatchLock(batch, ct);
        var ids = await db.TripReviewMediaOperations.AsNoTracking().Where(x => x.BatchId == batch).OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
        var rows = new List<TripReviewMediaOperation>();
        foreach (var id in ids) { var row = await LockedOperation(id, ct); if (row != null) rows.Add(row); }
        return rows;
    }
    private async Task<TripReviewMediaOperation?> LockedOperation(Guid id, CancellationToken ct)
    {
        // Force fresh persisted state instead of identity-map values after another context won.
        var tracked = db.ChangeTracker.Entries<TripReviewMediaOperation>().SingleOrDefault(x => x.Entity.Id == id);
        if (tracked != null) tracked.State = EntityState.Detached;
        return await db.TripReviewMediaOperations.FromSqlInterpolated($"SELECT * FROM social.TripReviewMediaOperations WITH(UPDLOCK,HOLDLOCK) WHERE operation_id={id}").SingleOrDefaultAsync(ct);
    }
}
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripReviews.Media;

public sealed record PreparedReviewMedia(Guid? BatchId, IReadOnlyList<ReviewMediaVersion> Operations);
public sealed class ReviewMediaCoordinator(IReviewImageInspector inspector, IReviewMediaJournal journal,
    IReviewMediaRecoveryJournal recovery, IReviewMediaStorage storage, TimeProvider time) : IReviewMediaCoordinator
{
    public Task<Result<PreparedReviewMedia>> PrepareAsync(long bookingId, long travelerId,
        IReadOnlyList<ReviewImageSource> images, CancellationToken ct) =>
        PrepareAsync(ReviewableRecordRef.CommerceBooking(bookingId), travelerId, images, ct);

    public async Task<Result<PreparedReviewMedia>> PrepareAsync(ReviewableRecordRef reviewableRecord,
        long travelerId, IReadOnlyList<ReviewImageSource> images, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        if (travelerId <= 0 || images.Count > TripReviewInputRules.MaximumPhotos) return InvalidInput();
        if (images.Count == 0) return Result<PreparedReviewMedia>.Success(new(null, []));
        var inspected = new List<(InspectedReviewImage Image, long InputBytes)>();
        foreach (var source in images)
        {
            using var counted = new CountingReadStream(source.Content);
            var inspection = await inspector.InspectAsync(source with { Content = counted }, ct);
            if (!inspection.IsAccepted) return InvalidInput();
            inspected.Add((inspection.Image!, counted.BytesRead));
        }
        ct.ThrowIfCancellationRequested();
        var batch = Guid.NewGuid();
        var operations = inspected.Select((image, index) => reviewableRecord.IsCommerceBooking
            ? TripReviewMediaOperation.Reserve(
                Guid.NewGuid(), batch, reviewableRecord.Id, travelerId, (byte)index,
                Guid.NewGuid().ToString("N"), image.Image.ContentType, image.Image.Extension,
                image.InputBytes, image.Image.Width, image.Image.Height, time.GetUtcNow())
            : TripReviewMediaOperation.ReserveForService(
                Guid.NewGuid(), batch, reviewableRecord.Id, travelerId, (byte)index,
                Guid.NewGuid().ToString("N"), image.Image.ContentType, image.Image.Extension,
                image.InputBytes, image.Image.Width, image.Image.Height, time.GetUtcNow())).ToArray();
        var reserved = await journal.ReserveAsync(reviewableRecord, travelerId, operations, ct);
        if (reserved.IsFailure) return StorageFailure();
        var complete = new List<ReviewMediaVersion>();
        try
        {
            for (var index = 0; index < operations.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var claim = await recovery.ClaimUploadAsync(operations[index].Id, travelerId, time.GetUtcNow(), ct);
                if (claim.IsFailure) { await Cleanup(); return StorageFailure(); }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var uploaded = await storage.UploadAsync(claim.Value.PublicId, inspected[index].Image, timeout.Token);
                ct.ThrowIfCancellationRequested();
                var recorded = await recovery.CompleteUploadAsync(claim.Value, travelerId, uploaded, time.GetUtcNow(), ct);
                if (uploaded.Outcome != ReviewMediaStorageOutcome.Success || recorded.IsFailure)
                {
                    if (uploaded.Outcome == ReviewMediaStorageOutcome.Success)
                    {
                        using var destroy = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await storage.DestroyAsync(claim.Value.PublicId, destroy.Token); }
                        catch (Exception) { /* Durable recovery retains the exact identity and uncertainty. */ }
                    }
                    await Cleanup();
                    return StorageFailure();
                }
                complete.Add(recorded.Value);
            }
            ct.ThrowIfCancellationRequested();
            return Result<PreparedReviewMedia>.Success(new(batch, complete));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await Cleanup(); throw; }
        catch (Exception)
        { await Cleanup(); return StorageFailure(); }

        async Task Cleanup()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await recovery.RequestCleanupAsync(
                batch, reviewableRecord, travelerId, time.GetUtcNow(), cleanup.Token);
            }
            catch (Exception) { /* Reserved rows and committed dispatch evidence remain scanner-recoverable. */ }
        }
    }
    private static Result<PreparedReviewMedia> InvalidInput() =>
        Result<PreparedReviewMedia>.Failure(ReviewMediaPreparationErrors.InvalidInput,
            "Review media input is invalid.");

    private static Result<PreparedReviewMedia> StorageFailure() =>
        Result<PreparedReviewMedia>.Failure(ReviewMediaPreparationErrors.StorageUnavailable,
            "Review media could not be prepared.");

    // Measures consumed input bytes, not the advisory HTTP length nor re-encoded output length.
    // The inspector already bounds reads. The caller still owns the original stream.
    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); BytesRead += read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var read = await inner.ReadAsync(buffer, ct); BytesRead += read; return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
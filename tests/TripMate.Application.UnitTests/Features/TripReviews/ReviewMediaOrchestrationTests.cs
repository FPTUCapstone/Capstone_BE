using Moq;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.TripReviews;

public sealed class ReviewMediaOrchestrationTests
{
    private readonly Mock<IReviewImageInspector> inspector = new();
    private readonly Mock<IReviewMediaJournal> journal = new();
    private readonly Mock<IReviewMediaRecoveryJournal> recovery = new();
    private readonly Mock<IReviewMediaStorage> storage = new();
    private ReviewMediaCoordinator Coordinator => new(inspector.Object, journal.Object, recovery.Object, storage.Object, TimeProvider.System);
    [Fact]
    public async Task Cleanup_ClaimsNextOperationOnlyAfterPreviousCompletes_AndCapsRunAtTwenty()
    {
        var outstanding = false;
        var count = 0;
        recovery.Setup(x => x.ClaimCleanupAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset _, int maximum, CancellationToken __) =>
            {
                Assert.Equal(1, maximum);
                Assert.False(outstanding);
                outstanding = true;
                count++;
                return (IReadOnlyList<ReviewMediaCleanupClaim>)[new(Guid.NewGuid(), Guid.NewGuid(), "exact", true)];
            });
        storage.Setup(x => x.ProbeAsync("exact", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.AlreadyAbsent));
        recovery.Setup(x => x.CompleteCleanupAsync(It.IsAny<ReviewMediaCleanupClaim>(), It.IsAny<ReviewMediaStorageOutcome>(), It.IsAny<bool>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { outstanding = false; return Result.Success(); });

        await new ReviewMediaRecoveryRunner(recovery.Object, storage.Object, TimeProvider.System).RunOnceAsync(default);

        Assert.Equal(20, count);
        Assert.False(outstanding);
    }
    private static ReviewImageSource Source() => new(new MemoryStream([1, 2, 3]), "private-name.png", "image/png", 999);
    private void Happy()
    {
        inspector.Setup(x => x.InspectAsync(It.IsAny<ReviewImageSource>(), It.IsAny<CancellationToken>())).Returns(async (ReviewImageSource s, CancellationToken ct) =>
        { using var bytes = new MemoryStream(); await s.Content.CopyToAsync(bytes, ct); return new(new([8, 9], "image/png", ".png", 1, 1), null); });
        journal.Setup(x => x.ReserveAsync(ReviewableRecordRef.CommerceBooking(1), 2,
            It.IsAny<IReadOnlyList<TripReviewMediaOperation>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result<IReadOnlyList<ReviewMediaVersion>>.Success([]));
        recovery.Setup(x => x.ClaimUploadAsync(It.IsAny<Guid>(), 2, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, long _, DateTimeOffset __, CancellationToken ___) => Result<ReviewMediaUploadClaim>.Success(new(id, Guid.NewGuid(), id.ToString("N"))));
        storage.Setup(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<InspectedReviewImage>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.Success, "https://example.test/image", 2));
        recovery.Setup(x => x.CompleteUploadAsync(It.IsAny<ReviewMediaUploadClaim>(), 2, It.IsAny<ReviewMediaStorageResult>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync((ReviewMediaUploadClaim c, long _, ReviewMediaStorageResult __, DateTimeOffset ___, CancellationToken ____) => Result<ReviewMediaVersion>.Success(new(c.OperationId, [1])));
        recovery.Setup(x => x.RequestCleanupAsync(It.IsAny<Guid>(), ReviewableRecordRef.CommerceBooking(1), 2,
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());
    }
    [Fact]
    public async Task Empty_DoesNotReserveOrCallProvider()
    { var result = await Coordinator.PrepareAsync(1, 2, [], default); Assert.True(result.IsSuccess); Assert.Null(result.Value.BatchId); journal.VerifyNoOtherCalls(); storage.VerifyNoOtherCalls(); }
    [Fact]
    public async Task InvalidLastImage_ReservesNothing()
    { Happy(); inspector.SetupSequence(x => x.InspectAsync(It.IsAny<ReviewImageSource>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ReviewImageInspection(new([1], "image/png", ".png", 1, 1), null)).ReturnsAsync(new ReviewImageInspection(null, ReviewImageRejection.InvalidImage)); var result = await Coordinator.PrepareAsync(1, 2, [Source(), Source()], default); Assert.True(result.IsFailure); Assert.Equal(ReviewMediaPreparationErrors.InvalidInput, result.ErrorCode); journal.VerifyNoOtherCalls(); storage.VerifyNoOtherCalls(); }
    [Fact]
    public async Task Success_UsesActualBytesAndOpaqueIdentity_ReturnsCompleteBatch()
    { Happy(); var result = await Coordinator.PrepareAsync(1, 2, [Source(), Source()], default); Assert.True(result.IsSuccess); Assert.Equal(2, result.Value.Operations.Count); journal.Verify(x => x.ReserveAsync(ReviewableRecordRef.CommerceBooking(1), 2, It.Is<IReadOnlyList<TripReviewMediaOperation>>(o => o.Count == 2 && o.All(v => v.InputByteLength == 3 && v.PublicId.Length == 32)), It.IsAny<CancellationToken>())); }
    [Theory]
    [InlineData(ReviewMediaStorageOutcome.TransientFailure)]
    [InlineData(ReviewMediaStorageOutcome.PermanentFailure)]
    [InlineData(ReviewMediaStorageOutcome.UnknownOutcome)]
    public async Task FailedUpload_CleansWholeBatch(ReviewMediaStorageOutcome outcome)
    { Happy(); storage.SetupSequence(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<InspectedReviewImage>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.Success, "https://example.test/a", 2)).ReturnsAsync(new ReviewMediaStorageResult(outcome)); var result = await Coordinator.PrepareAsync(1, 2, [Source(), Source()], default); Assert.True(result.IsFailure); Assert.Equal(ReviewMediaPreparationErrors.StorageUnavailable, result.ErrorCode); recovery.Verify(x => x.RequestCleanupAsync(It.IsAny<Guid>(), ReviewableRecordRef.CommerceBooking(1), 2, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once); }
    [Fact]
    public async Task ServiceFailedUpload_UsesServiceParentForReservationAndCleanup()
    {
        var reviewableRecord = ReviewableRecordRef.ServiceBooking(7);
        IReadOnlyList<TripReviewMediaOperation>? reservedOperations = null;
        inspector.Setup(x => x.InspectAsync(It.IsAny<ReviewImageSource>(), It.IsAny<CancellationToken>()))
            .Returns(async (ReviewImageSource source, CancellationToken ct) =>
            {
                using var bytes = new MemoryStream();
                await source.Content.CopyToAsync(bytes, ct);
                return new ReviewImageInspection(new([8, 9], "image/png", ".png", 1, 1), null);
            });
        journal.Setup(x => x.ReserveAsync(reviewableRecord, 2,
                It.IsAny<IReadOnlyList<TripReviewMediaOperation>>(), It.IsAny<CancellationToken>()))
            .Callback((ReviewableRecordRef _, long _, IReadOnlyList<TripReviewMediaOperation> operations,
                CancellationToken _) => reservedOperations = operations)
            .ReturnsAsync(Result<IReadOnlyList<ReviewMediaVersion>>.Success([]));
        recovery.Setup(x => x.ClaimUploadAsync(It.IsAny<Guid>(), 2, It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, long _, DateTimeOffset __, CancellationToken ___) =>
                Result<ReviewMediaUploadClaim>.Success(new(id, Guid.NewGuid(), id.ToString("N"))));
        storage.Setup(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<InspectedReviewImage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.TransientFailure));
        recovery.Setup(x => x.CompleteUploadAsync(It.IsAny<ReviewMediaUploadClaim>(), 2,
                It.IsAny<ReviewMediaStorageResult>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ReviewMediaVersion>.Failure("upload_failed", "Upload failed."));
        recovery.Setup(x => x.RequestCleanupAsync(It.IsAny<Guid>(), reviewableRecord, 2,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await Coordinator.PrepareAsync(reviewableRecord, 2, [Source()], default);

        Assert.True(result.IsFailure);
        Assert.NotNull(reservedOperations);
        Assert.Collection(reservedOperations,
            operation =>
            {
                Assert.Null(operation.BookingId);
                Assert.Equal(7, operation.ServiceBookingId);
            });
        recovery.Verify(x => x.RequestCleanupAsync(It.IsAny<Guid>(), reviewableRecord, 2,
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        recovery.Verify(x => x.RequestCleanupAsync(It.IsAny<Guid>(), It.IsAny<long>(), 2,
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task StaleSuccess_DestroyExactAssetAndCleanup_NoPublication()
    { Happy(); recovery.Setup(x => x.CompleteUploadAsync(It.IsAny<ReviewMediaUploadClaim>(), 2, It.IsAny<ReviewMediaStorageResult>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result<ReviewMediaVersion>.Failure("stale", "stale")); Assert.True((await Coordinator.PrepareAsync(1, 2, [Source()], default)).IsFailure); storage.Verify(x => x.DestroyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once); }
    [Fact]
    public async Task CancellationAfterDispatch_PersistsCleanupWithIndependentToken()
    { Happy(); using var cancel = new CancellationTokenSource(); storage.Setup(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<InspectedReviewImage>(), It.IsAny<CancellationToken>())).Returns((string _, InspectedReviewImage __, CancellationToken ct) => { cancel.Cancel(); throw new OperationCanceledException(ct); }); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Coordinator.PrepareAsync(1, 2, [Source()], cancel.Token)); recovery.Verify(x => x.RequestCleanupAsync(It.IsAny<Guid>(), ReviewableRecordRef.CommerceBooking(1), 2, It.IsAny<DateTimeOffset>(), It.Is<CancellationToken>(c => !c.IsCancellationRequested)), Times.Once); }
    [Fact]
    public async Task UploadThrowsAfterDispatch_NoPartialReferences_WholeBatchCleanup()
    {
        Happy();
        storage.SetupSequence(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<InspectedReviewImage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.Success, "https://example.test/a", 2))
            .ThrowsAsync(new IOException("connection lost"));

        var result = await Coordinator.PrepareAsync(1, 2, [Source(), Source()], default);

        Assert.True(result.IsFailure);
        recovery.Verify(x => x.RequestCleanupAsync(It.IsAny<Guid>(), ReviewableRecordRef.CommerceBooking(1), 2, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        recovery.Verify(x => x.CompleteUploadAsync(It.IsAny<ReviewMediaUploadClaim>(), 2, It.IsAny<ReviewMediaStorageResult>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task RecordUploadThrows_RequestsDurableCleanup_NoMoreUploads()
    {
        Happy();
        recovery.Setup(x => x.CompleteUploadAsync(It.IsAny<ReviewMediaUploadClaim>(), 2, It.IsAny<ReviewMediaStorageResult>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database unavailable"));

        Assert.True((await Coordinator.PrepareAsync(1, 2, [Source(), Source()], default)).IsFailure);

        storage.Verify(x => x.UploadAsync(It.IsAny<string>(), It.IsAny<InspectedReviewImage>(), It.IsAny<CancellationToken>()), Times.Once);
        recovery.Verify(x => x.RequestCleanupAsync(It.IsAny<Guid>(), ReviewableRecordRef.CommerceBooking(1), 2, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentObservation_DoesNotInventTerminalCertainty(bool known)
    { var claim = new ReviewMediaCleanupClaim(Guid.NewGuid(), Guid.NewGuid(), "opaque", known); recovery.SetupSequence(x => x.ClaimCleanupAsync(It.IsAny<DateTimeOffset>(), 1, It.IsAny<CancellationToken>())).ReturnsAsync([claim]).ReturnsAsync([]); storage.Setup(x => x.ProbeAsync("opaque", It.IsAny<CancellationToken>())).ReturnsAsync(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.AlreadyAbsent)); await new ReviewMediaRecoveryRunner(recovery.Object, storage.Object, TimeProvider.System).RunOnceAsync(default); recovery.Verify(x => x.CompleteCleanupAsync(claim, ReviewMediaStorageOutcome.AlreadyAbsent, false, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once); storage.Verify(x => x.DestroyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never); }
    [Fact]
    public async Task LateSuccess_DeleteAndConfirmAbsence_ThenSupplyTerminalEvidence()
    { var claim = new ReviewMediaCleanupClaim(Guid.NewGuid(), Guid.NewGuid(), "exact", false); recovery.SetupSequence(x => x.ClaimCleanupAsync(It.IsAny<DateTimeOffset>(), 1, It.IsAny<CancellationToken>())).ReturnsAsync([claim]).ReturnsAsync([]); storage.SetupSequence(x => x.ProbeAsync("exact", It.IsAny<CancellationToken>())).ReturnsAsync(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.Success)).ReturnsAsync(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.AlreadyAbsent)); storage.Setup(x => x.DestroyAsync("exact", It.IsAny<CancellationToken>())).ReturnsAsync(new ReviewMediaStorageResult(ReviewMediaStorageOutcome.Success)); await new ReviewMediaRecoveryRunner(recovery.Object, storage.Object, TimeProvider.System).RunOnceAsync(default); recovery.Verify(x => x.CompleteCleanupAsync(claim, ReviewMediaStorageOutcome.AlreadyAbsent, true, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once); }
}
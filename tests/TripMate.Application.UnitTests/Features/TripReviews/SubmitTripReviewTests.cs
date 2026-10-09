using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Application.Features.TripReviews.Submit;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripReviews;

public sealed class SubmitTripReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Unauthenticated_StopsBeforeContextMediaLockAndWrite()
    {
        await using var fixture = await Fixture.CreateAsync(userId: null);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Unauthorized);
        fixture.Reader.Calls.Should().Be(0);
        fixture.Media.Calls.Should().Be(0);
        fixture.WriteLock.Calls.Should().Be(0);
        fixture.Db.TripReviews.Should().BeEmpty();
    }

    [Theory]
    [InlineData(UserRole.Traveler, AccountStatus.Locked, "Traveler")]
    [InlineData(UserRole.Administrator, AccountStatus.Active, "Traveler")]
    [InlineData(UserRole.Traveler, AccountStatus.Active, "Administrator")]
    public async Task InactiveOrNonTraveler_StopsBeforeMedia(UserRole role, AccountStatus status, string claim)
    {
        await using var fixture = await Fixture.CreateAsync(role, status, claim: claim);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Forbidden);
        fixture.Media.Calls.Should().Be(0);
        fixture.WriteLock.Calls.Should().Be(0);
    }

    [Fact]
    public async Task MissingOrForeignBooking_FailsWithoutMedia()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = null;

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.BookingNotFound);
        fixture.Media.Calls.Should().Be(0);
        fixture.WriteLock.Calls.Should().Be(0);
    }

    [Fact]
    public async Task NonCompletedBooking_FailsBeforeMedia()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = Context(status: "Confirmed");

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.BookingNotCompleted);
        fixture.Media.Calls.Should().Be(0);
        fixture.WriteLock.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EarlyNewDuplicate_WinsBeforeInvalidInputAndAllExpensiveWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = Context(parent: Parent());

        var result = await fixture.HandleAsync(Command(rating: 0, title: " "));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Duplicate);
        fixture.Media.Calls.Should().Be(0);
        fixture.Journal.AdoptCalls.Should().Be(0);
        fixture.WriteLock.Calls.Should().Be(0);
        fixture.Db.TripReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task EarlyLegacyDuplicate_WinsBeforeImageInspectionAndJournalAllocation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = Context(legacy: [new(90, Review.TargetTypePoi, 5, 4, "old", Now)]);

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Duplicate);
        fixture.Media.Calls.Should().Be(0);
        fixture.Journal.AdoptCalls.Should().Be(0);
        fixture.WriteLock.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AcceptedSubmission_PersistsExactNormalizedTextAndActivePolicyVersion()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.HandleAsync(Command(title: "  Chuyến đi  ", content: "  Rất vui  "));

        result.IsSuccess.Should().BeTrue();
        var saved = await fixture.Db.TripReviews.SingleAsync();
        saved.Title.Should().Be("Chuyến đi");
        saved.Content.Should().Be("Rất vui");
        saved.PolicyVersion.Should().Be(ReviewContentPolicy.ActiveVersion);
        result.Value.Title.Should().Be(saved.Title);
        result.Value.Content.Should().Be(saved.Content);
    }

    [Fact]
    public async Task RejectedSubmission_FailsBeforeMediaAndDoesNotPublish()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Moderator.Next = ReviewContentModerationResult.Rejected(
            ReviewPolicyCategory.ThreatOrCallForPhysicalHarm);

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.PolicyRejected);
        fixture.Moderator.Calls.Should().Be(1);
        fixture.Media.Calls.Should().Be(0);
        fixture.WriteLock.Calls.Should().Be(0);
        fixture.Db.TripReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task UnavailableSubmission_FailsClosedBeforeMediaAndDoesNotPublish()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Moderator.Next = ReviewContentModerationResult.Unavailable();

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.PolicyUnavailable);
        fixture.Media.Calls.Should().Be(0);
        fixture.Db.TripReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidZeroPhotoSubmission_DoesNotUseMediaSubsystem()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.HandleAsync(Command());

        result.IsSuccess.Should().BeTrue();
        fixture.Media.Calls.Should().Be(0);
        fixture.Journal.AdoptCalls.Should().Be(0);
        fixture.Journal.CleanupCalls.Should().Be(0);
    }

    [Fact]
    public async Task ValidPhotoSubmission_PreparesOutsideLockAndAdoptsExactBatchInsideLock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var batch = Guid.NewGuid();
        var version = new ReviewMediaVersion(Guid.NewGuid(), [1]);
        fixture.Media.Next = Result.Success(new PreparedReviewMedia(batch, [version]));

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.IsSuccess.Should().BeTrue();
        fixture.Media.Calls.Should().Be(1);
        fixture.Media.LockWasHeldAtCall.Should().BeFalse();
        fixture.Journal.AdoptedBatch.Should().Be(batch);
        fixture.Journal.AdoptedVersions.Should().Equal(version);
        fixture.Journal.AdoptWasInsideLock.Should().BeTrue();
    }

    [Fact]
    public async Task MediaPreparationFailure_DoesNotEnterPublicationTransaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Media.Next = Result.Failure<PreparedReviewMedia>(ReviewMediaPreparationErrors.StorageUnavailable, "failed");

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.StorageUnavailable);
        fixture.WriteLock.Calls.Should().Be(0);
        fixture.Db.TripReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidImagePreparation_MapsToPublicInvalidInput()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Media.Next = Result.Failure<PreparedReviewMedia>(ReviewMediaPreparationErrors.InvalidInput, "invalid");

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.InvalidInput);
        fixture.WriteLock.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AdoptionFailure_IsSanitizedToPublicStorageUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Media.Next = Result.Success(new PreparedReviewMedia(Guid.NewGuid(),
            [new ReviewMediaVersion(Guid.NewGuid(), [1])]));
        fixture.Journal.AdoptResult = Result.Failure(ReviewMediaErrors.InvalidState, "internal state");

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.StorageUnavailable);
        result.ErrorCode.Should().NotStartWith("review_media.");
        fixture.Journal.CleanupCalls.Should().Be(1);
    }

    [Fact]
    public async Task CleanupFailure_DoesNotMaskFinalDuplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Media.Next = Result.Success(new PreparedReviewMedia(Guid.NewGuid(),
            [new ReviewMediaVersion(Guid.NewGuid(), [1])]));
        fixture.Reader.Values.Enqueue(Context());
        fixture.Reader.Values.Enqueue(Context(parent: Parent()));
        fixture.Journal.CleanupException = new IOException("cleanup unavailable");

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Duplicate);
        fixture.Journal.CleanupCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EarlyLegacyConflict_StopsBeforeMedia(bool foreignLegacy, bool parentOverlap)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = Context(
            parent: parentOverlap ? Parent() : null,
            legacy: parentOverlap ? [Legacy()] : [],
            foreignLegacy: foreignLegacy);

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.LegacyConflict);
        fixture.Media.Calls.Should().Be(0);
    }

    [Fact]
    public async Task MultipleOwnedLegacyRows_AreConservativeConflict()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = Context(legacy: [Legacy(90), Legacy(91)]);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.LegacyConflict);
    }

    [Fact]
    public async Task MismatchedTourLegacyTarget_IsConflict()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = Context(tourId: 44,
            legacy: [new(90, Review.TargetTypeTour, 999, 4, "old", Now)]);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.LegacyConflict);
    }

    [Fact]
    public async Task InconsistentContext_PrecedesExistingReview()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = Context(parent: Parent(), itineraryOwner: 999);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.InconsistentContext);
        fixture.Media.Calls.Should().Be(0);
    }

    [Fact]
    public async Task FinalInconsistentContext_PrecedesDuplicateAndCleansPreparedMedia()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Media.Next = Result.Success(new PreparedReviewMedia(Guid.NewGuid(),
            [new ReviewMediaVersion(Guid.NewGuid(), [1])]));
        fixture.Reader.Values.Enqueue(Context());
        fixture.Reader.Values.Enqueue(Context(parent: Parent(), itineraryOwner: 999));

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.InconsistentContext);
        fixture.Journal.CleanupCalls.Should().Be(1);
    }

    [Fact]
    public async Task FinalForeignLegacyConflict_CleansPreparedMedia()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Media.Next = Result.Success(new PreparedReviewMedia(Guid.NewGuid(),
            [new ReviewMediaVersion(Guid.NewGuid(), [1])]));
        fixture.Reader.Values.Enqueue(Context());
        fixture.Reader.Values.Enqueue(Context(foreignLegacy: true));

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.LegacyConflict);
        fixture.Journal.CleanupCalls.Should().Be(1);
        fixture.Journal.AdoptCalls.Should().Be(0);
    }

    [Fact]
    public async Task CspPersistsOnlyWithOwnedServerProvenance_AndPoiEvidenceIsNotInvented()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = Context(csp: true);

        var accepted = await fixture.HandleAsync(Command(csp: 5));
        var rejected = await fixture.HandleAsync(Command(bookingId: 11, pois: [new(99, 5)]));

        accepted.IsSuccess.Should().BeTrue();
        (await fixture.Db.TripReviews.SingleAsync()).CspRating.Should().Be(5);
        rejected.ErrorCode.Should().Be(TripReviewErrorCodes.BookingNotFound);
    }

    [Fact]
    public async Task FinalDuplicateAfterMediaPreparation_MarksOnlyPreparedBatchForCleanup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var batch = Guid.NewGuid();
        var version = new ReviewMediaVersion(Guid.NewGuid(), [2]);
        fixture.Media.Next = Result.Success(new PreparedReviewMedia(batch, [version]));
        fixture.Reader.Values.Enqueue(Context());
        fixture.Reader.Values.Enqueue(Context(parent: Parent()));

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Duplicate);
        fixture.Journal.CleanupBatch.Should().Be(batch);
        fixture.Journal.CleanupVersions.Should().Equal(version);
        fixture.Journal.CleanupWasInsideLock.Should().BeFalse();
        fixture.Journal.AdoptCalls.Should().Be(0);
    }

    [Fact]
    public async Task FinalLockedRecheck_RejectsTravelerThatBecameInactiveDuringMediaPreparation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var batch = Guid.NewGuid();
        fixture.Media.Next = Result.Success(new PreparedReviewMedia(batch,
            [new ReviewMediaVersion(Guid.NewGuid(), [4])]));
        fixture.Media.OnPrepare = () =>
        {
            fixture.Db.Users.Single().Status = AccountStatus.Locked;
            fixture.Db.SaveChanges();
            fixture.Reader.Value = Context(travelerStatus: AccountStatus.Locked);
        };

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Forbidden);
        fixture.Db.TripReviews.Should().BeEmpty();
        fixture.Journal.CleanupCalls.Should().Be(1);
    }

    [Fact]
    public async Task RoutePacingSpecifiedWhenUnavailable_FailsWithRoutePacingUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.HandleAsync(Command(pacing: RoutePacingFeedback.WellPaced));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.RoutePacingUnavailable);
        fixture.WriteLock.Calls.Should().Be(0);
        fixture.Db.TripReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task PoiRatingsSpecifiedWhenUnavailable_FailsWithPoiUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.HandleAsync(Command(pois: [new(101, 5)]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.PoiUnavailable);
        fixture.WriteLock.Calls.Should().Be(0);
        fixture.Db.TripReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task CspRatingSpecifiedWhenUnavailable_FailsWithCspIneligible()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.HandleAsync(Command(csp: 5));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.CspIneligible);
        fixture.WriteLock.Calls.Should().Be(0);
        fixture.Db.TripReviews.Should().BeEmpty();
    }

    [Fact]
    public async Task ExpectedBookingUniqueViolation_MappedToDuplicateErrorCode()
    {
        await using var fixture = await Fixture.CreateAsync();
        var batch = Guid.NewGuid();
        var version = new ReviewMediaVersion(Guid.NewGuid(), [3]);
        fixture.Media.Next = Result.Success(new PreparedReviewMedia(batch, [version]));
        fixture.Db.ThrowOnTransaction = new DbUpdateException(
            "Unique constraint failed",
            new FakeSqlException(2601, "Violation of UNIQUE KEY constraint 'UX_TripReviews_Booking'."));
        fixture.PersistenceErrors.IsDuplicate = true;

        var result = await fixture.HandleAsync(Command(photos: [Photo()]));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Duplicate);
        fixture.Journal.CleanupCalls.Should().Be(1);
        fixture.Journal.CleanupBatch.Should().Be(batch);
    }

    private sealed class FakeSqlException(int number, string message) : Exception(message)
    {
        public int Number { get; } = number;
    }

    [Fact]
    public async Task ArbitraryDbException_NotMappedToDuplicate_ThrowsDirectly()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.ThrowOnTransaction = new DbUpdateException(
            "FK violation",
            new Exception("Violation of FOREIGN KEY constraint 'FK_Something'."));

        var act = () => fixture.HandleAsync(Command());

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData(547)]
    [InlineData(0)]
    public async Task NamedConstraintWithWrongSqlNumber_IsNotMappedToDuplicate(int number)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.ThrowOnTransaction = new DbUpdateException("write failed",
            new FakeSqlException(number, "Violation of UNIQUE KEY constraint 'UX_TripReviews_Booking'."));

        var act = () => fixture.HandleAsync(Command());

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task NamedConstraintWithoutSqlNumber_IsNotMappedToDuplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.ThrowOnTransaction = new DbUpdateException("write failed",
            new Exception("Violation of UNIQUE KEY constraint 'UX_TripReviews_Booking'."));

        var act = () => fixture.HandleAsync(Command());

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private static SubmitTripReviewCommand Command(long bookingId = 10, int rating = 5,
        string title = "Title", string content = "Content", IReadOnlyList<TripReviewPoiInput>? pois = null,
        IReadOnlyList<TripReviewPhotoInput>? photos = null, RoutePacingFeedback? pacing = null, int? csp = null) =>
        new(bookingId, rating, title, content, pois ?? [], photos ?? [], pacing, csp);

    private static TripReviewPhotoInput Photo() => new("photo.jpg", "image/jpeg", 4, new byte[] { 1, 2, 3, 4 });

    private static TripReview Parent() => TripReview.CreatePublished(10, 1, null, 20, 4,
        "Old", "Review", null, null, false, "Owner", null, Now.AddDays(-1));

    private static LegacyTripReviewEntryDto Legacy(long id = 90) =>
        new(id, Review.TargetTypePoi, 5, 4, "old", Now);

    private static TripReviewContextData Context(string status = TripReviewContextValues.Completed,
        TripReview? parent = null, IReadOnlyList<LegacyTripReviewEntryDto>? legacy = null, bool csp = false,
        bool foreignLegacy = false, bool foreignParent = false, long? itineraryOwner = 1, long? tourId = null,
        AccountStatus travelerStatus = AccountStatus.Active) =>
        new(new TripReviewBookingData
        {
            BookingId = 10,
            BookingStatus = status,
            TourScheduleId = tourId.HasValue ? 30 : null,
            TourId = tourId,
            ItineraryId = tourId.HasValue ? null : 20,
            ItineraryOwnerId = tourId.HasValue ? null : itineraryOwner,
            ItinerarySourceType = csp ? Itinerary.CspGeneratedSourceType : Itinerary.ManualSourceType,
            SchedulingRequestId = csp ? 30 : null,
            SchedulingRequestOwnerId = csp ? 1 : null,
            TravelerRole = nameof(UserRole.Traveler),
            TravelerStatus = travelerStatus.ToString(),
            TravelerFullName = "Owner",
        }, parent, legacy ?? [], foreignLegacy, foreignParent);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(TestDbContext db, FakeCurrentUserService currentUser)
        {
            Db = db;
            Reader = new Reader { Value = Context() };
            WriteLock = new Lock();
            Journal = new Journal(WriteLock);
            Media = new Media(WriteLock);
            Moderator = new Moderator();
            PersistenceErrors = new PersistenceErrorClassifier();
            Handler = new SubmitTripReviewCommandHandler(db, currentUser,
                new FakeDateTimeProvider { UtcNow = Now }, Reader, WriteLock, PersistenceErrors,
                Journal, Media, Moderator);
            Db.OnTransactionCompleted = () => WriteLock.IsHeld = false;
        }

        public TestDbContext Db { get; }
        public Reader Reader { get; }
        public Lock WriteLock { get; }
        public Journal Journal { get; }
        public Media Media { get; }
        public Moderator Moderator { get; }
        public PersistenceErrorClassifier PersistenceErrors { get; }
        public SubmitTripReviewCommandHandler Handler { get; }

        public static async Task<Fixture> CreateAsync(UserRole role = UserRole.Traveler,
            AccountStatus status = AccountStatus.Active, long? userId = 1, string? claim = nameof(UserRole.Traveler))
        {
            var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            db.Users.Add(new User { Id = 1, Role = role, Status = status, FullName = "Owner" });
            await db.SaveChangesAsync();
            return new Fixture(db, new FakeCurrentUserService { UserId = userId, Role = claim });
        }

        public Task<Result<NewTripReviewDto>> HandleAsync(SubmitTripReviewCommand command) => Handler.Handle(command, default);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class Reader : ITripReviewContextReader
    {
        public TripReviewContextData? Value { get; set; }
        public Queue<TripReviewContextData?> Values { get; } = new();
        public int Calls { get; private set; }
        public Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Values.Count > 0 ? Values.Dequeue() : Value);
        }

        public Task<TripReviewContextData?> ReadOwnedForUpdateAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Values.Count > 0 ? Values.Dequeue() : Value);
        }
    }

    private sealed class Lock : ITripReviewWriteLock
    {
        public int Calls { get; private set; }
        public bool IsHeld { get; set; }
        public Task AcquireAsync(long bookingId, CancellationToken cancellationToken)
        {
            Calls++;
            IsHeld = true;
            return Task.CompletedTask;
        }
    }

    private sealed class PersistenceErrorClassifier : ITripReviewPersistenceErrorClassifier
    {
        public bool IsDuplicate { get; set; }
        public bool IsBookingDuplicate(DbUpdateException exception) => IsDuplicate;
    }

    private sealed class Moderator : IReviewContentModerator
    {
        public string ActivePolicyVersion => ReviewContentPolicy.ActiveVersion;
        public int Calls { get; private set; }
        public ReviewText? Seen { get; private set; }
        public ReviewContentModerationResult Next { get; set; } =
            ReviewContentModerationResult.Accepted(ReviewContentPolicy.ActiveVersion);

        public Task<ReviewContentModerationResult> ScreenAsync(
            ReviewText text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Seen = text;
            return Task.FromResult(Next);
        }
    }

    private sealed class Media(Lock writeLock) : IReviewMediaCoordinator
    {
        public int Calls { get; private set; }
        public bool LockWasHeldAtCall { get; private set; }
        public Action? OnPrepare { get; set; }
        public Result<PreparedReviewMedia> Next { get; set; } = Result.Success(new PreparedReviewMedia(null, []));
        public Task<Result<PreparedReviewMedia>> PrepareAsync(long bookingId, long travelerId,
            IReadOnlyList<ReviewImageSource> images, CancellationToken cancellationToken)
        {
            Calls++;
            LockWasHeldAtCall = writeLock.IsHeld;
            OnPrepare?.Invoke();
            return Task.FromResult(Next);
        }
    }

    private sealed class Journal(Lock writeLock) : IReviewMediaJournal
    {
        public int AdoptCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        public Guid? AdoptedBatch { get; private set; }
        public IReadOnlyList<ReviewMediaVersion>? AdoptedVersions { get; private set; }
        public bool AdoptWasInsideLock { get; private set; }
        public Guid? CleanupBatch { get; private set; }
        public IReadOnlyList<ReviewMediaVersion>? CleanupVersions { get; private set; }
        public bool CleanupWasInsideLock { get; private set; }
        public Result AdoptResult { get; set; } = Result.Success();
        public Exception? CleanupException { get; set; }
        public Task<Result<IReadOnlyList<ReviewMediaVersion>>> ReserveAsync(long bookingId, long travelerId,
            IReadOnlyList<TripReviewMediaOperation> operations, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result> AdoptAsync(Guid batchId, long travelerId, IReadOnlyList<ReviewMediaVersion> expected,
            TripReview parent, DateTimeOffset now, CancellationToken ct)
        {
            AdoptCalls++;
            AdoptedBatch = batchId;
            AdoptedVersions = expected;
            AdoptWasInsideLock = writeLock.IsHeld;
            return Task.FromResult(AdoptResult);
        }
        public Task<Result> MarkCleanupPendingAsync(Guid batchId, long bookingId, long travelerId,
            IReadOnlyList<ReviewMediaVersion> expected, DateTimeOffset now, CancellationToken ct)
        {
            CleanupCalls++;
            CleanupBatch = batchId;
            CleanupVersions = expected;
            CleanupWasInsideLock = writeLock.IsHeld;
            if (CleanupException is not null)
                throw CleanupException;
            return Task.FromResult(Result.Success());
        }
    }
}
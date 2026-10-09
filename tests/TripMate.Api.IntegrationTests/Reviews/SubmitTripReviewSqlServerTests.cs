using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.GetContext;
using TripMate.Application.Features.TripReviews.Media;
using TripMate.Application.Features.TripReviews.Submit;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Reviews.Media;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class SubmitTripReviewSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task Submit_PersistsNormalizedScreenedParentWithoutLegacyMirror()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();
        var handler = Handler(context, new SqlServerTripReviewContextReader(context), new EmptyMedia());

        var result = await handler.Handle(Command(title: "  Chuyến đi  ", content: "  Rất vui  "), default);

        result.IsSuccess.Should().BeTrue();
        await using var verify = database.CreateDbContext();
        var parent = await verify.TripReviews.AsNoTracking().SingleAsync();
        parent.Title.Should().Be("Chuyến đi");
        parent.Content.Should().Be("Rất vui");
        parent.PolicyVersion.Should().Be(ReviewContentPolicy.ActiveVersion);
        (await verify.Reviews.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task BookedTourItineraryCompatibility_PublishesTourSubjectAndOnlyTourAggregate()
    {
        await using var database = await SeedAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.OperatorProfiles(user_id,company_name,tax_code,business_license_no,approval_status)
            VALUES(2,N'Compatibility Operator',N'TM79-R7-TAX',N'TM79-R7-LICENSE','Approved');
            INSERT commerce.Tours(operator_user_id,title,base_price,duration_days,status)
            VALUES(2,N'Compatibility Tour',0,1,'Approved');
            INSERT commerce.TourSchedules(tour_id,start_datetime,end_datetime,total_capacity,status)
            VALUES(1,'2026-09-01','2026-09-02',10,'Completed');
            UPDATE planning.Itineraries
            SET source_type='BookedTour',source_tour_id=1
            WHERE itinerary_id=1;
            UPDATE commerce.Bookings SET tour_schedule_id=1 WHERE booking_id=1;
            """);
        await using var context = database.CreateDbContext();
        var handler = Handler(context, new SqlServerTripReviewContextReader(context), new EmptyMedia());

        var result = await handler.Handle(Command(title: "Tour title", content: "Great tour experience"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Subject.Should().Be(new TripReviewSubjectDto(TripReviewContextValues.TourSubject, 1));
        await using var verify = database.CreateDbContext();
        var saved = await verify.TripReviews.AsNoTracking().SingleAsync();
        saved.TourId.Should().Be(1);
        saved.ItineraryId.Should().BeNull();
        (await TripReviewAggregateReader.ReadTourAsync(
            verify,
            1,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)).Value
            .Should().Be(new TripReviewAggregateDto(5m, 1));
        (await verify.Reviews.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task FailureAfterParentInsert_RollsBackParentChildrenAndAdoption()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();
        var batch = Guid.NewGuid();
        var prepared = new PreparedReviewMedia(batch, [new(Guid.NewGuid(), [1])]);
        var journal = new FailAfterParentInsertJournal(context);
        var handler = Handler(context, new SqlServerTripReviewContextReader(context),
            new FixedMedia(prepared), journal);

        var result = await handler.Handle(Command(photos: [Photo()]), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(TripReviewErrorCodes.StorageUnavailable);
        journal.CleanupCalls.Should().Be(1);
        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.AsNoTracking().CountAsync()).Should().Be(0);
        (await verify.TripReviewMedia.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task SameBookingRace_PublishesOne_AdoptsWinnerAndMarksLoserCleanupPending()
    {
        await using var database = await SeedAsync();
        var firstPrepared = await SeedUploadedBatchAsync(database, 1);
        var secondPrepared = await SeedUploadedBatchAsync(database, 1);
        var gate = new EarlyReadGate(2);

        await using var firstContext = database.CreateDbContext();
        await using var secondContext = database.CreateDbContext();
        var firstReader = new GatedReader(new SqlServerTripReviewContextReader(firstContext), gate);
        var secondReader = new GatedReader(new SqlServerTripReviewContextReader(secondContext), gate);
        var first = Handler(firstContext, firstReader, new FixedMedia(firstPrepared));
        var second = Handler(secondContext, secondReader, new FixedMedia(secondPrepared));

        var results = await Task.WhenAll(
            first.Handle(Command(photos: [Photo()]), default),
            second.Handle(Command(photos: [Photo()]), default));

        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Single(result => result.IsFailure).ErrorCode.Should().Be(TripReviewErrorCodes.Duplicate);
        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.AsNoTracking().CountAsync()).Should().Be(1);
        (await verify.TripReviewMedia.AsNoTracking().CountAsync()).Should().Be(1);
        var operations = await verify.TripReviewMediaOperations.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        operations.Should().ContainSingle(x => x.State == TripReviewMediaOperation.Adopted);
        var loser = operations.Should().ContainSingle(x => x.State == TripReviewMediaOperation.CleanupPending).Subject;
        var loserRecovery = await verify.Set<TripReviewMediaRecovery>().AsNoTracking()
            .SingleAsync(x => x.OperationId == loser.Id);
        loserRecovery.UploadOutcome.Should().Be(TripReviewMediaRecovery.Succeeded);
        loserRecovery.Exhausted.Should().BeFalse();
    }

    [SqlServerFact]
    public async Task DifferentBookingLocks_CanBeHeldConcurrently()
    {
        await using var database = await SeedAsync();
        await using var firstContext = database.CreateDbContext();
        await using var secondContext = database.CreateDbContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var gate = new EarlyReadGate(2);

        async Task Hold(ApplicationDbContext context, long bookingId)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(timeout.Token);
            await new SqlServerTripReviewWriteLock(context).AcquireAsync(bookingId, timeout.Token);
            await gate.SignalAndWaitAsync(timeout.Token);
            await transaction.RollbackAsync(timeout.Token);
        }

        await Task.WhenAll(Hold(firstContext, 1), Hold(secondContext, 2));
    }

    [SqlServerFact]
    public async Task SameNumericId_InCommerceAndServiceNamespaces_UsesIndependentLocks()
    {
        await using var database = await SeedAsync();
        await using var commerceContext = database.CreateDbContext();
        await using var serviceContext = database.CreateDbContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var gate = new EarlyReadGate(2);

        async Task Hold(ApplicationDbContext context, ReviewableRecordRef reviewableRecord)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(timeout.Token);
            await new SqlServerTripReviewWriteLock(context).AcquireAsync(reviewableRecord, timeout.Token);
            await gate.SignalAndWaitAsync(timeout.Token);
            await transaction.RollbackAsync(timeout.Token);
        }

        await Task.WhenAll(
            Hold(commerceContext, ReviewableRecordRef.CommerceBooking(1)),
            Hold(serviceContext, ReviewableRecordRef.ServiceBooking(1)));
    }

    [SqlServerFact]
    public async Task WriteLock_RejectsUseWithoutCallerOwnedTransaction()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();

        var action = () => new SqlServerTripReviewWriteLock(context).AcquireAsync(1, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*caller-owned transaction*");
    }

    [SqlServerFact]
    public async Task SameTravelerDifferentBookings_CanSubmitConcurrently()
    {
        await using var database = await SeedAsync();
        var gate = new EarlyReadGate(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var firstContext = database.CreateDbContext();
        await using var secondContext = database.CreateDbContext();
        var first = Handler(firstContext,
            new LockedPairReader(new SqlServerTripReviewContextReader(firstContext), gate), new EmptyMedia());
        var second = Handler(secondContext,
            new LockedPairReader(new SqlServerTripReviewContextReader(secondContext), gate), new EmptyMedia());

        var results = await Task.WhenAll(
            first.Handle(Command(bookingId: 1), timeout.Token),
            second.Handle(Command(bookingId: 2), timeout.Token));

        results.Should().OnlyContain(result => result.IsSuccess);
        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.AsNoTracking().Select(x => x.BookingId).OrderBy(x => x).ToListAsync())
            .Should().Equal(1, 2);
    }

    [SqlServerFact]
    public async Task LockedEvidenceRows_BlockConcurrentAuthorizationMutationUntilCommit()
    {
        await using var database = await SeedAsync();
        await using var submitContext = database.CreateDbContext();
        await using var mutationContext = database.CreateDbContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var gate = new LockedReadGate(new SqlServerTripReviewContextReader(submitContext));
        var handler = Handler(submitContext, gate, new EmptyMedia());

        var submission = handler.Handle(Command(), timeout.Token);
        await gate.ReadCompleted.Task.WaitAsync(timeout.Token);

        var mutationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutation = Task.Run(async () =>
        {
            mutationStarted.TrySetResult();
            return await mutationContext.Database.ExecuteSqlRawAsync(
                "UPDATE dbo.Users SET status='Locked' WHERE user_id=1", timeout.Token);
        }, timeout.Token);
        await mutationStarted.Task.WaitAsync(timeout.Token);

        var premature = await Task.WhenAny(mutation, Task.Delay(TimeSpan.FromMilliseconds(300), timeout.Token));
        premature.Should().NotBe(mutation, "the final authorization evidence must remain locked through commit");

        gate.Release.TrySetResult();
        (await submission).IsSuccess.Should().BeTrue();
        (await mutation).Should().Be(1);
    }

    [SqlServerFact]
    public async Task AmbiguousAndMismatchedLegacyRows_AreConflictsAndRemainUnchanged()
    {
        await using var ambiguousDatabase = await SeedAsync();
        await ambiguousDatabase.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating,comment)
            VALUES(1,'POI',10,1,4,N'First'),(1,'POI',11,1,5,N'Second');
            """);
        await using (var context = ambiguousDatabase.CreateDbContext())
        {
            var result = await Handler(context, new SqlServerTripReviewContextReader(context), new EmptyMedia())
                .Handle(Command(), default);
            result.ErrorCode.Should().Be(TripReviewErrorCodes.LegacyConflict);
        }
        await using (var verify = ambiguousDatabase.CreateDbContext())
        {
            (await verify.Reviews.AsNoTracking().CountAsync()).Should().Be(2);
            (await verify.TripReviews.AsNoTracking().CountAsync()).Should().Be(0);
        }

        await using var mismatchedDatabase = await SeedAsync();
        await mismatchedDatabase.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating,comment)
            VALUES(1,'Tour',999,1,4,N'Mismatched target');
            """);
        await using (var context = mismatchedDatabase.CreateDbContext())
        {
            var result = await Handler(context, new SqlServerTripReviewContextReader(context), new EmptyMedia())
                .Handle(Command(), default);
            result.ErrorCode.Should().Be(TripReviewErrorCodes.LegacyConflict);
        }
        await using (var verify = mismatchedDatabase.CreateDbContext())
        {
            (await verify.Reviews.AsNoTracking().CountAsync()).Should().Be(1);
            (await verify.TripReviews.AsNoTracking().CountAsync()).Should().Be(0);
        }
    }

    [SqlServerFact]
    public async Task LegacyAppearingAfterEarlyRead_IsRejectedByLockedRecheck()
    {
        await using var database = await SeedAsync();
        await using var context = database.CreateDbContext();
        var media = new CallbackMedia(async () => await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating,comment)
            VALUES(1,'POI',10,1,4,N'Concurrent legacy');
            """));
        var handler = Handler(context, new SqlServerTripReviewContextReader(context), media);

        var result = await handler.Handle(Command(photos: [Photo()]), default);

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Duplicate);
        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.CountAsync()).Should().Be(0);
        (await verify.Reviews.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    public async Task AmbiguousLegacyAppearingAfterMediaPreparation_IsConflictAndCleansPreparedBatch()
    {
        await using var database = await SeedAsync();
        var prepared = await SeedUploadedBatchAsync(database, 1);
        await using var context = database.CreateDbContext();
        var media = new CallbackPreparedMedia(async () => await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating,comment)
            VALUES(1,'POI',10,1,4,N'First concurrent legacy'),
                  (1,'POI',11,1,5,N'Second concurrent legacy');
            """), prepared);
        var handler = Handler(context, new SqlServerTripReviewContextReader(context), media);

        var result = await handler.Handle(Command(photos: [Photo()]), default);

        result.ErrorCode.Should().Be(TripReviewErrorCodes.LegacyConflict);
        await using var verify = database.CreateDbContext();
        (await verify.TripReviews.CountAsync()).Should().Be(0);
        (await verify.Reviews.CountAsync()).Should().Be(2);
        (await verify.TripReviewMediaOperations.SingleAsync()).State
            .Should().Be(TripReviewMediaOperation.CleanupPending);
    }

    [SqlServerFact]
    public async Task CommittedButLostResponse_IsRecoveredByGet_AndLaterPostIsDuplicate()
    {
        await using var database = await SeedAsync();
        await using (var firstContext = database.CreateDbContext())
        {
            _ = await Handler(firstContext, new SqlServerTripReviewContextReader(firstContext), new EmptyMedia())
                .Handle(Command(), default);
        }

        await using var recoveryContext = database.CreateDbContext();
        var reader = new SqlServerTripReviewContextReader(recoveryContext);
        var recovered = await new GetTripReviewContextQueryHandler(recoveryContext, new CurrentUser(),
            new Clock(), reader).Handle(new(1), default);
        var later = await Handler(recoveryContext, reader, new EmptyMedia()).Handle(Command(), default);

        recovered.IsSuccess.Should().BeTrue();
        recovered.Value.Review.Should().BeOfType<NewTripReviewDto>();
        later.ErrorCode.Should().Be(TripReviewErrorCodes.Duplicate);
        (await recoveryContext.TripReviews.CountAsync()).Should().Be(1);
    }

    private static SubmitTripReviewCommandHandler Handler(ApplicationDbContext context,
        ITripReviewContextReader reader, IReviewMediaCoordinator media, IReviewMediaJournal? journal = null) =>
        new(context, new CurrentUser(), new Clock(), reader, new SqlServerTripReviewWriteLock(context),
            new SqlServerTripReviewPersistenceErrorClassifier(),
            journal ?? new SqlServerReviewMediaJournal(context), media, new AcceptedModerator());

    private static SubmitTripReviewCommand Command(long bookingId = 1, string title = "Title",
        string content = "Content", IReadOnlyList<TripReviewPhotoInput>? photos = null) =>
        new(bookingId, 5, title, content, [], photos ?? []);

    private static TripReviewPhotoInput Photo() =>
        new("photo.jpg", "image/jpeg", 4, new byte[] { 1, 2, 3, 4 });

    private static async Task<PreparedReviewMedia> SeedUploadedBatchAsync(SqlServerTestDatabase database, long bookingId)
    {
        await using var context = database.CreateDbContext();
        var batch = Guid.NewGuid();
        var operation = TripReviewMediaOperation.Reserve(Guid.NewGuid(), batch, bookingId, 1, 0,
            Guid.NewGuid().ToString("N"), "image/jpeg", ".jpg", 4, 1, 1, Now);
        operation.TryRecordUpload("https://example.test/review.jpg", 4, Now).Should().BeTrue();
        var recovery = TripReviewMediaRecovery.Create(operation.Id);
        var uploadFence = Guid.NewGuid();
        recovery.TryClaimUpload(uploadFence, Now).Should().BeTrue();
        recovery.TryObserveUpload(uploadFence, succeeded: true).Should().BeTrue();
        context.TripReviewMediaOperations.Add(operation);
        context.Set<TripReviewMediaRecovery>().Add(recovery);
        await context.SaveChangesAsync();
        return new(batch, [new(operation.Id, operation.Version.ToArray())]);
    }

    private static async Task<SqlServerTestDatabase> SeedAsync()
    {
        var database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.ExecuteNonQueryAsync("""
                INSERT dbo.Users(role,status,email,full_name)
                VALUES('Traveler','Active',N'owner@test.invalid',N'Owner'),
                      ('TourOperator','Active',N'operator@test.invalid',N'Operator');
                INSERT planning.Itineraries(traveler_user_id,source_type,title)
                VALUES(1,'Manual',N'First'),(1,'Manual',N'Second');
                INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
                VALUES('TM79-SUBMIT-1',1,1,0,0,'Completed'),('TM79-SUBMIT-2',1,2,0,0,'Completed');
                """);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private sealed class CurrentUser : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => nameof(UserRole.Traveler);
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class AcceptedModerator : IReviewContentModerator
    {
        public string ActivePolicyVersion => ReviewContentPolicy.ActiveVersion;
        public Task<ReviewContentModerationResult> ScreenAsync(
            ReviewText text, CancellationToken cancellationToken) =>
            Task.FromResult(ReviewContentModerationResult.Accepted(ActivePolicyVersion));
    }

    private sealed class EmptyMedia : IReviewMediaCoordinator
    {
        public Task<Result<PreparedReviewMedia>> PrepareAsync(long bookingId, long travelerId,
            IReadOnlyList<ReviewImageSource> images, CancellationToken ct) =>
            Task.FromResult(Result.Success(new PreparedReviewMedia(null, [])));
    }

    private sealed class FixedMedia(PreparedReviewMedia prepared) : IReviewMediaCoordinator
    {
        public Task<Result<PreparedReviewMedia>> PrepareAsync(long bookingId, long travelerId,
            IReadOnlyList<ReviewImageSource> images, CancellationToken ct) => Task.FromResult(Result.Success(prepared));
    }

    private sealed class CallbackMedia(Func<Task> callback) : IReviewMediaCoordinator
    {
        public async Task<Result<PreparedReviewMedia>> PrepareAsync(long bookingId, long travelerId,
            IReadOnlyList<ReviewImageSource> images, CancellationToken ct)
        {
            await callback();
            return Result.Success(new PreparedReviewMedia(null, []));
        }
    }

    private sealed class CallbackPreparedMedia(Func<Task> callback, PreparedReviewMedia prepared)
        : IReviewMediaCoordinator
    {
        public async Task<Result<PreparedReviewMedia>> PrepareAsync(long bookingId, long travelerId,
            IReadOnlyList<ReviewImageSource> images, CancellationToken ct)
        {
            await callback();
            return Result.Success(prepared);
        }
    }

    private sealed class FailAfterParentInsertJournal(ApplicationDbContext context) : IReviewMediaJournal
    {
        public int CleanupCalls { get; private set; }
        public Task<Result<IReadOnlyList<ReviewMediaVersion>>> ReserveAsync(long bookingId, long travelerId,
            IReadOnlyList<TripReviewMediaOperation> operations, CancellationToken ct) => throw new NotSupportedException();
        public async Task<Result> AdoptAsync(Guid batchId, long travelerId,
            IReadOnlyList<ReviewMediaVersion> expected, TripReview parent, DateTimeOffset now, CancellationToken ct)
        {
            context.TripReviews.Add(parent);
            await context.SaveChangesAsync(ct);
            return Result.Failure(ReviewMediaErrors.InvalidState, "simulated adoption failure");
        }
        public Task<Result> MarkCleanupPendingAsync(Guid batchId, long bookingId, long travelerId,
            IReadOnlyList<ReviewMediaVersion> expected, DateTimeOffset now, CancellationToken ct)
        {
            CleanupCalls++;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class GatedReader(ITripReviewContextReader inner, EarlyReadGate gate) : ITripReviewContextReader
    {
        private int _calls;
        public async Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken)
        {
            var value = await inner.ReadOwnedAsync(bookingId, travelerUserId, cancellationToken);
            if (Interlocked.Increment(ref _calls) == 1)
                await gate.SignalAndWaitAsync(cancellationToken);
            return value;
        }

        public Task<TripReviewContextData?> ReadOwnedForUpdateAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken) =>
            inner.ReadOwnedForUpdateAsync(bookingId, travelerUserId, cancellationToken);
    }

    private sealed class LockedReadGate(ITripReviewContextReader inner) : ITripReviewContextReader
    {
        public TaskCompletionSource ReadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken) =>
            inner.ReadOwnedAsync(bookingId, travelerUserId, cancellationToken);

        public async Task<TripReviewContextData?> ReadOwnedForUpdateAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReadOwnedForUpdateAsync(bookingId, travelerUserId, cancellationToken);
            ReadCompleted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class LockedPairReader(ITripReviewContextReader inner, EarlyReadGate gate)
        : ITripReviewContextReader
    {
        public Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken) =>
            inner.ReadOwnedAsync(bookingId, travelerUserId, cancellationToken);

        public async Task<TripReviewContextData?> ReadOwnedForUpdateAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken)
        {
            var value = await inner.ReadOwnedForUpdateAsync(bookingId, travelerUserId, cancellationToken);
            await gate.SignalAndWaitAsync(cancellationToken);
            return value;
        }
    }

    private sealed class EarlyReadGate(int participants)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == participants)
                _released.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }
    }
}
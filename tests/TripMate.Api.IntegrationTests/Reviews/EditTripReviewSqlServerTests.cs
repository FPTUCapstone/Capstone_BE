using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Edit;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Reviews;

public sealed class EditTripReviewSqlServerTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeDeadline = Created.AddDays(7).AddTicks(-1);

    [SqlServerFact]
    public async Task BeforeDeadline_EditPersistsNormalizedFieldsAndPreservesImmutableGraph()
    {
        await using var database = await SeedAsync(withMedia: true, policyVersion: "tm79-review-text-v1");
        var before = await ReadSnapshotAsync(database, 1);
        await using var context = database.CreateDbContext();
        var handler = Handler(context, new SqlServerTripReviewContextReader(context), BeforeDeadline);

        var result = await handler.Handle(Command(before.Version, title: "  Đà Nẵng  đẹp  ",
            content: "  Rất\n  đáng nhớ  ", publishDisplayName: true), default);

        result.IsSuccess.Should().BeTrue();
        await using var verify = database.CreateDbContext();
        var review = await verify.TripReviews.AsNoTracking().SingleAsync(x => x.BookingId == 1);
        review.OverallRating.Should().Be(5);
        review.Title.Should().Be("Đà Nẵng  đẹp");
        review.Content.Should().Be("Rất\n  đáng nhớ");
        review.PolicyVersion.Should().Be(ReviewContentPolicy.ActiveVersion);
        review.PublicDisplayName.Should().Be("Owner Name");
        review.CreatedAtUtc.Should().Be(before.CreatedAtUtc);
        review.EditDeadlineUtc.Should().Be(before.DeadlineUtc);
        review.TourId.Should().Be(before.TourId);
        review.ItineraryId.Should().Be(before.ItineraryId);
        review.RoutePacing.Should().Be(before.RoutePacing);
        review.CspRating.Should().Be(before.CspRating);
        review.Version.Should().NotEqual(before.Version);
        result.Value.Version.Should().Be(Convert.ToBase64String(review.Version));

        var media = await verify.TripReviewMedia.AsNoTracking()
            .Include(item => item.Operation).SingleAsync();
        media.Id.Should().Be(before.MediaId);
        before.OperationId.Should().NotBeNull();
        media.OperationId.Should().Be(before.OperationId!.Value);
        media.SortOrder.Should().Be(before.MediaSortOrder);
        media.Operation.State.Should().Be(TripReviewMediaOperation.Adopted);
        media.Operation.DeliveryUrl.Should().Be(before.DeliveryUrl);
        result.Value.Media.Should().ContainSingle(item => item.MediaId == media.Id
            && item.DeliveryUrl == before.DeliveryUrl);
        (await verify.Reviews.AsNoTracking().CountAsync()).Should().Be(0,
            "the current schema has no linked POI-child contract and edit must not invent one");
    }

    [SqlServerTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AtOrAfterDeadline_IsRejectedAndRowIsUnchanged(int ticksAfterDeadline)
    {
        await using var database = await SeedAsync();
        var before = await ReadSnapshotAsync(database, 1);
        await using var context = database.CreateDbContext();
        var handler = Handler(context, new SqlServerTripReviewContextReader(context),
            before.DeadlineUtc.AddTicks(ticksAfterDeadline));

        var result = await handler.Handle(Command(before.Version), default);

        result.ErrorCode.Should().Be(TripReviewErrorCodes.EditExpired);
        (await ReadSnapshotAsync(database, 1)).Should().BeEquivalentTo(before);
    }

    [SqlServerFact]
    public async Task StaleRowVersion_IsRejectedAndRowIsUnchanged()
    {
        await using var database = await SeedAsync();
        var before = await ReadSnapshotAsync(database, 1);
        var stale = before.Version.ToArray();
        stale[^1] ^= 0xff;
        await using var context = database.CreateDbContext();

        var result = await Handler(context, new SqlServerTripReviewContextReader(context), BeforeDeadline)
            .Handle(Command(stale), default);

        result.ErrorCode.Should().Be(TripReviewErrorCodes.StaleVersion);
        (await ReadSnapshotAsync(database, 1)).Should().BeEquivalentTo(before);
    }

    [SqlServerTheory]
    [InlineData(false, true, "Renamed Account")]
    [InlineData(true, false, "R. A.")]
    [InlineData(false, false, "O. N.")]
    [InlineData(true, true, "Owner Name")]
    public async Task DisplaySnapshot_FollowsPreferenceChangeAndRenameRules(
        bool originalPreference, bool requestedPreference, string expectedLabel)
    {
        await using var database = await SeedAsync(originalPreference: originalPreference);
        await database.ExecuteNonQueryAsync("UPDATE dbo.Users SET full_name=N'Renamed Account' WHERE user_id=1;");
        var before = await ReadSnapshotAsync(database, 1);
        await using var context = database.CreateDbContext();

        var result = await Handler(context, new SqlServerTripReviewContextReader(context), BeforeDeadline)
            .Handle(Command(before.Version, publishDisplayName: requestedPreference), default);

        result.IsSuccess.Should().BeTrue();
        (await ReadSnapshotAsync(database, 1)).PublicDisplayName.Should().Be(expectedLabel);
    }

    [SqlServerFact]
    public async Task ForeignTravelerWithKnownVersion_CannotReadOrModifyReview()
    {
        await using var database = await SeedAsync(addForeignTraveler: true);
        var before = await ReadSnapshotAsync(database, 1);
        await using var context = database.CreateDbContext();

        var result = await Handler(context, new SqlServerTripReviewContextReader(context), BeforeDeadline,
            userId: 3).Handle(Command(before.Version), default);

        result.ErrorCode.Should().Be(TripReviewErrorCodes.BookingNotFound);
        (await ReadSnapshotAsync(database, 1)).Should().BeEquivalentTo(before);
    }

    [SqlServerFact]
    public async Task LegacyOnlyReview_IsGatedAndLegacyRowIsUnchanged()
    {
        await using var database = await SeedAsync(withParent: false);
        await database.ExecuteNonQueryAsync("""
            INSERT social.Reviews(traveler_user_id,target_type,target_id,booking_id,rating,comment)
            VALUES(1,'POI',10,1,4,N'Legacy text');
            """);
        await using var context = database.CreateDbContext();

        var result = await Handler(context, new SqlServerTripReviewContextReader(context), BeforeDeadline)
            .Handle(Command(new byte[8]), default);

        result.ErrorCode.Should().Be(TripReviewErrorCodes.LegacyConflict);
        await using var verify = database.CreateDbContext();
        var legacy = await verify.Reviews.AsNoTracking().SingleAsync();
        legacy.Rating.Should().Be(4);
        legacy.Comment.Should().Be("Legacy text");
        (await verify.TripReviews.CountAsync()).Should().Be(0);
    }

    [SqlServerFact]
    public async Task PersistenceFailure_RollsBackAndLeavesPreviousPublicationUnchanged()
    {
        await using var database = await SeedAsync();
        var before = await ReadSnapshotAsync(database, 1);
        await using var context = database.CreateDbContext(new FailEditSaveChanges());
        var handler = Handler(context, new SqlServerTripReviewContextReader(context), BeforeDeadline);

        var action = () => handler.Handle(Command(before.Version), default);

        await action.Should().ThrowAsync<DbUpdateException>().WithMessage("*simulated edit failure*");
        (await ReadSnapshotAsync(database, 1)).Should().BeEquivalentTo(before);
    }

    [SqlServerFact]
    public async Task SameVersionConcurrentEdits_ExactlyOneSucceedsAndLoserIsStale()
    {
        await using var database = await SeedAsync(withMedia: true);
        var before = await ReadSnapshotAsync(database, 1);
        var gate = new AsyncGate(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var firstContext = database.CreateDbContext();
        await using var secondContext = database.CreateDbContext();
        var first = Handler(firstContext,
            new EarlyReadGate(new SqlServerTripReviewContextReader(firstContext), gate), BeforeDeadline);
        var second = Handler(secondContext,
            new EarlyReadGate(new SqlServerTripReviewContextReader(secondContext), gate), BeforeDeadline);

        var results = await Task.WhenAll(
            first.Handle(Command(before.Version, title: "First winner", content: "First body"), timeout.Token),
            second.Handle(Command(before.Version, title: "Second winner", content: "Second body"), timeout.Token));

        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Single(result => result.IsFailure).ErrorCode.Should().Be(TripReviewErrorCodes.StaleVersion);
        var winner = results.Single(result => result.IsSuccess).Value;
        var final = await ReadSnapshotAsync(database, 1);
        final.Title.Should().Be(winner.Title);
        final.Content.Should().Be(winner.Content);
        final.Title.Should().NotBe(final.Title == "First winner" ? "Second winner" : "First winner");
        final.MediaId.Should().Be(before.MediaId);
        final.OperationId.Should().Be(before.OperationId);
        final.RoutePacing.Should().Be(before.RoutePacing);
        final.CspRating.Should().Be(before.CspRating);
    }

    [SqlServerFact]
    public async Task DifferentBookingEdits_CanReachLockedBoundaryConcurrentlyAndBothCommit()
    {
        await using var database = await SeedAsync(secondParent: true);
        var firstBefore = await ReadSnapshotAsync(database, 1);
        var secondBefore = await ReadSnapshotAsync(database, 2);
        var gate = new AsyncGate(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var firstContext = database.CreateDbContext();
        await using var secondContext = database.CreateDbContext();
        var first = Handler(firstContext,
            new LockedReadGate(new SqlServerTripReviewContextReader(firstContext), gate), BeforeDeadline);
        var second = Handler(secondContext,
            new LockedReadGate(new SqlServerTripReviewContextReader(secondContext), gate), BeforeDeadline);

        var results = await Task.WhenAll(
            first.Handle(Command(firstBefore.Version, bookingId: 1, title: "First booking"), timeout.Token),
            second.Handle(Command(secondBefore.Version, bookingId: 2, title: "Second booking"), timeout.Token));

        results.Should().OnlyContain(result => result.IsSuccess);
        (await ReadSnapshotAsync(database, 1)).Title.Should().Be("First booking");
        (await ReadSnapshotAsync(database, 2)).Title.Should().Be("Second booking");
    }

    private static EditTripReviewCommandHandler Handler(ApplicationDbContext context,
        ITripReviewContextReader reader, DateTimeOffset now, long userId = 1) =>
        new(context, new CurrentUser(userId), new Clock(now), reader,
            new SqlServerTripReviewWriteLock(context), new AcceptedModerator());

    private static EditTripReviewCommand Command(byte[] version, long bookingId = 1,
        string title = "Edited Title", string content = "Edited Content",
        bool publishDisplayName = false) =>
        new(bookingId, 5, title, content, publishDisplayName, Convert.ToBase64String(version));

    private static async Task<SqlServerTestDatabase> SeedAsync(bool withParent = true,
        bool withMedia = false, bool secondParent = false, bool originalPreference = false,
        bool addForeignTraveler = false, string? policyVersion = null)
    {
        var database = await SqlServerTestDatabase.CreateAsync();
        try
        {
            await database.ExecuteNonQueryAsync($"""
                INSERT dbo.Users(role,status,email,full_name)
                VALUES('Traveler','Active',N'owner@test.invalid',N'Owner Name'),
                      ('TourOperator','Active',N'operator@test.invalid',N'Operator')
                      {((addForeignTraveler) ? ",('Traveler','Active',N'foreign@test.invalid',N'Foreign User')" : string.Empty)};
                INSERT planning.Itineraries(traveler_user_id,source_type,title)
                VALUES(1,'Manual',N'First'),(1,'Manual',N'Second');
                INSERT commerce.Bookings(booking_code,traveler_user_id,itinerary_id,unit_price,total_amount,status)
                VALUES('TM79-EDIT-1',1,1,0,0,'Completed'),('TM79-EDIT-2',1,2,0,0,'Completed');
                """);

            if (withParent)
            {
                await using var context = database.CreateDbContext();
                var first = TripReview.CreatePublished(1, 1, null, 1, 4,
                    "Old Title", "Old Content", RoutePacingFeedback.WellPaced, 4,
                    originalPreference, "Owner Name", policyVersion, Created);
                context.TripReviews.Add(first);
                await context.SaveChangesAsync();

                if (withMedia)
                {
                    var operation = TripReviewMediaOperation.Reserve(Guid.NewGuid(), Guid.NewGuid(), 1, 1, 0,
                        Guid.NewGuid().ToString("N"), "image/jpeg", ".jpg", 4, 1, 1, Created);
                    operation.TryRecordUpload("https://example.test/original.jpg", 4, Created).Should().BeTrue();
                    operation.TryAdopt(Created).Should().BeTrue();
                    context.TripReviewMediaOperations.Add(operation);
                    context.TripReviewMedia.Add(TripReviewMedia.Link(first, operation, Created));
                    await context.SaveChangesAsync();
                }

                if (secondParent)
                {
                    context.TripReviews.Add(TripReview.CreatePublished(2, 1, null, 2, 3,
                        "Second Old", "Second Content", null, null, false,
                        "Owner Name", null, Created));
                    await context.SaveChangesAsync();
                }
            }

            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<ReviewSnapshot> ReadSnapshotAsync(SqlServerTestDatabase database, long bookingId)
    {
        await using var context = database.CreateDbContext();
        var review = await context.TripReviews.AsNoTracking().SingleAsync(x => x.BookingId == bookingId);
        var media = await context.TripReviewMedia.AsNoTracking()
            .Include(item => item.Operation)
            .SingleOrDefaultAsync(item => item.TripReviewId == review.Id);
        return new(review.BookingId ?? throw new InvalidOperationException("Expected a commerce review."),
            review.TravelerUserId, review.TourId, review.ItineraryId,
            review.OverallRating, review.Title, review.Content, review.RoutePacing, review.CspRating,
            review.PublishDisplayName, review.PublicDisplayName, review.PolicyVersion,
            review.CreatedAtUtc, review.EditDeadlineUtc, review.UpdatedAtUtc, review.Version.ToArray(),
            media?.Id, media?.OperationId, media?.SortOrder, media?.Operation.DeliveryUrl);
    }

    private sealed record ReviewSnapshot(long BookingId, long TravelerUserId, long? TourId, long? ItineraryId,
        byte OverallRating, string Title, string Content, RoutePacingFeedback? RoutePacing, byte? CspRating,
        bool PublishDisplayName, string PublicDisplayName, string? PolicyVersion,
        DateTimeOffset CreatedAtUtc, DateTimeOffset DeadlineUtc, DateTimeOffset UpdatedAtUtc, byte[] Version,
        long? MediaId, Guid? OperationId, byte? MediaSortOrder, string? DeliveryUrl);

    private sealed class CurrentUser(long userId) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Role => nameof(UserRole.Traveler);
    }

    private sealed class Clock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class AcceptedModerator : IReviewContentModerator
    {
        public string ActivePolicyVersion => ReviewContentPolicy.ActiveVersion;
        public Task<ReviewContentModerationResult> ScreenAsync(
            ReviewText text, CancellationToken cancellationToken) =>
            Task.FromResult(ReviewContentModerationResult.Accepted(ActivePolicyVersion));
    }

    private sealed class FailEditSaveChanges : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(
                new DbUpdateException("simulated edit failure"));
    }

    private sealed class EarlyReadGate(ITripReviewContextReader inner, AsyncGate gate)
        : ITripReviewContextReader
    {
        public async Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReadOwnedAsync(bookingId, travelerUserId, cancellationToken);
            await gate.SignalAndWaitAsync(cancellationToken);
            return result;
        }

        public Task<TripReviewContextData?> ReadOwnedForUpdateAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken) =>
            inner.ReadOwnedForUpdateAsync(bookingId, travelerUserId, cancellationToken);
    }

    private sealed class LockedReadGate(ITripReviewContextReader inner, AsyncGate gate)
        : ITripReviewContextReader
    {
        public Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken) =>
            inner.ReadOwnedAsync(bookingId, travelerUserId, cancellationToken);

        public async Task<TripReviewContextData?> ReadOwnedForUpdateAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReadOwnedForUpdateAsync(bookingId, travelerUserId, cancellationToken);
            await gate.SignalAndWaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class AsyncGate(int participants)
    {
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == participants)
                _released.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }
    }
}
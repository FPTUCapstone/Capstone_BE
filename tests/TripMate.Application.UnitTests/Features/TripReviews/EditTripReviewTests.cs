using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Edit;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripReviews;

public sealed class EditTripReviewTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeDeadline = Created.AddDays(7).AddTicks(-1);
    private static readonly byte[] OriginalVersion = [0, 0, 0, 0, 0, 0, 0, 1];

    [Fact]
    public async Task Unauthenticated_StopsBeforeContextLockAndWrite()
    {
        await using var fixture = await Fixture.CreateAsync(userId: null);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Unauthorized);
        fixture.Reader.Calls.Should().Be(0);
        fixture.WriteLock.Calls.Should().Be(0);
        await fixture.AssertOriginalAsync();
    }

    [Theory]
    [InlineData(UserRole.Traveler, AccountStatus.Locked, "Traveler")]
    [InlineData(UserRole.Administrator, AccountStatus.Active, "Traveler")]
    [InlineData(UserRole.Traveler, AccountStatus.Active, "Administrator")]
    public async Task InactiveOrNonTraveler_StopsBeforeContextAndWrite(
        UserRole role, AccountStatus status, string claim)
    {
        await using var fixture = await Fixture.CreateAsync(role: role, status: status, claim: claim);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Forbidden);
        fixture.Reader.Calls.Should().Be(0);
        fixture.WriteLock.Calls.Should().Be(0);
        await fixture.AssertOriginalAsync();
    }

    [Fact]
    public async Task MissingReview_ReturnsNotFoundWithoutWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = fixture.Context(parent: null);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.BookingNotFound);
        fixture.WriteLock.Calls.Should().Be(0);
        await fixture.AssertOriginalAsync();
    }

    [Fact]
    public async Task ForeignTraveler_CannotReadOrModifyReview()
    {
        await using var fixture = await Fixture.CreateAsync(userId: 2, addSecondTraveler: true);
        fixture.Reader.Value = null;

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.BookingNotFound);
        fixture.WriteLock.Calls.Should().Be(0);
        await fixture.AssertOriginalAsync();
    }

    [Fact]
    public async Task LegacyReview_IsFailSafeGatedAndUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Value = fixture.Context(parent: null, legacy: [Legacy()]);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.LegacyConflict);
        fixture.WriteLock.Calls.Should().Be(0);
        fixture.Db.Reviews.Should().BeEmpty();
        await fixture.AssertOriginalAsync();
    }

    [Theory]
    [InlineData(0, "Title", "Content")]
    [InlineData(6, "Title", "Content")]
    [InlineData(5, "   ", "Content")]
    [InlineData(5, "Title", "   ")]
    public async Task InvalidStructuralCommand_FailsBeforeLockAndPreservesPublication(
        int rating, string title, string content)
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.HandleAsync(Command(rating, title, content));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.InvalidInput);
        fixture.WriteLock.Calls.Should().Be(0);
        await fixture.AssertOriginalAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task BeforeDeadline_ValidBoundaryRatingsSucceed(int rating)
    {
        await using var fixture = await Fixture.CreateAsync(now: BeforeDeadline);

        var result = await fixture.HandleAsync(Command(rating));

        result.IsSuccess.Should().BeTrue();
        var saved = await fixture.ReloadAsync();
        saved.OverallRating.Should().Be((byte)rating);
        saved.CreatedAtUtc.Should().Be(Created);
        saved.EditDeadlineUtc.Should().Be(Created.AddDays(7));
    }

    [Fact]
    public async Task ExactDeadline_IsRejectedAndPreservesPublication()
    {
        await using var fixture = await Fixture.CreateAsync(now: Created.AddDays(7));

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be("trip_review.edit_expired");
        await fixture.AssertOriginalAsync();
    }

    [Fact]
    public async Task AfterDeadline_IsRejectedAndPreservesPublication()
    {
        await using var fixture = await Fixture.CreateAsync(now: Created.AddDays(7).AddTicks(1));

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be("trip_review.edit_expired");
        await fixture.AssertOriginalAsync();
    }

    [Fact]
    public async Task StaleCanonicalVersion_IsRejectedAndPreservesPublication()
    {
        await using var fixture = await Fixture.CreateAsync();
        var stale = Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0, 0, 0, 2 });

        var result = await fixture.HandleAsync(Command(version: stale));

        result.ErrorCode.Should().Be("trip_review.stale_version");
        await fixture.AssertOriginalAsync();
    }

    [Fact]
    public async Task MalformedVersion_IsStructuralFailureBeforeLock()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.HandleAsync(Command(version: "not-base64"));

        result.ErrorCode.Should().Be(TripReviewErrorCodes.InvalidInput);
        fixture.WriteLock.Calls.Should().Be(0);
        await fixture.AssertOriginalAsync();
    }

    [Fact]
    public async Task PaddedUnicodeText_IsNormalizedOnceAndInteriorWhitespaceIsPreserved()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.HandleAsync(Command(title: "  Đà Nẵng  đẹp  ", content: "  Rất\n  đáng nhớ  "));

        result.IsSuccess.Should().BeTrue();
        var saved = await fixture.ReloadAsync();
        saved.Title.Should().Be("Đà Nẵng  đẹp");
        saved.Content.Should().Be("Rất\n  đáng nhớ");
        result.Value.Title.Should().Be(saved.Title);
        result.Value.Content.Should().Be(saved.Content);
    }

    [Fact]
    public async Task ChangedText_IsAcceptedAndKeepsActivePolicyVersion()
    {
        await using var fixture = await Fixture.CreateAsync(policyVersion: "tm79-review-text-v1");

        var result = await fixture.HandleAsync(Command());

        result.IsSuccess.Should().BeTrue();
        fixture.Moderator.Calls.Should().Be(1);
        (await fixture.ReloadAsync()).PolicyVersion.Should().Be(ReviewContentPolicy.ActiveVersion);
    }

    [Fact]
    public async Task RejectedEdit_PreservesPriorAcceptedPublication()
    {
        await using var fixture = await Fixture.CreateAsync(policyVersion: ReviewContentPolicy.ActiveVersion);
        fixture.Moderator.Next = ReviewContentModerationResult.Rejected(
            ReviewPolicyCategory.TargetedDegradingHarassment);

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.PolicyRejected);
        await fixture.AssertOriginalAsync();
        (await fixture.ReloadAsync()).PolicyVersion.Should().Be(ReviewContentPolicy.ActiveVersion);
    }

    [Fact]
    public async Task UnavailableEdit_PreservesPriorAcceptedPublication()
    {
        await using var fixture = await Fixture.CreateAsync(policyVersion: ReviewContentPolicy.ActiveVersion);
        fixture.Moderator.Next = ReviewContentModerationResult.Unavailable();

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.PolicyUnavailable);
        await fixture.AssertOriginalAsync();
    }

    [Fact]
    public async Task RatingOnlyEdit_WithCurrentPolicy_DoesNotRescreen()
    {
        await using var fixture = await Fixture.CreateAsync(policyVersion: ReviewContentPolicy.ActiveVersion);

        var result = await fixture.HandleAsync(Command(rating: 3, title: "Old Title", content: "Old Content"));

        result.IsSuccess.Should().BeTrue();
        fixture.Moderator.Calls.Should().Be(0);
        (await fixture.ReloadAsync()).PolicyVersion.Should().Be(ReviewContentPolicy.ActiveVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("old-policy")]
    public async Task SameText_WithMissingOrOldPolicy_Rescreens(string? policyVersion)
    {
        await using var fixture = await Fixture.CreateAsync(policyVersion: policyVersion);

        var result = await fixture.HandleAsync(Command(title: "Old Title", content: "Old Content"));

        result.IsSuccess.Should().BeTrue();
        fixture.Moderator.Calls.Should().Be(1);
        (await fixture.ReloadAsync()).PolicyVersion.Should().Be(ReviewContentPolicy.ActiveVersion);
    }

    [Fact]
    public async Task DisplayPreferenceFalseToTrue_UsesCurrentFullName()
    {
        await using var fixture = await Fixture.CreateAsync(profileName: "Nguyễn Tiến Đạt");

        var result = await fixture.HandleAsync(Command(publishDisplayName: true));

        result.IsSuccess.Should().BeTrue();
        (await fixture.ReloadAsync()).PublicDisplayName.Should().Be("Nguyễn Tiến Đạt");
    }

    [Fact]
    public async Task DisplayPreferenceTrueToFalse_UsesCurrentInitials()
    {
        await using var fixture = await Fixture.CreateAsync(
            originalPublishDisplayName: true, profileName: "Nguyễn Tiến Đạt");

        var result = await fixture.HandleAsync(Command(publishDisplayName: false));

        result.IsSuccess.Should().BeTrue();
        (await fixture.ReloadAsync()).PublicDisplayName.Should().Be("N. T. Đ.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePreferenceAfterAccountRename_PreservesOriginalSnapshot(bool preference)
    {
        await using var fixture = await Fixture.CreateAsync(
            originalPublishDisplayName: preference, profileName: "Original Name");
        fixture.Db.Users.Single().FullName = "Renamed Account";
        await fixture.Db.SaveChangesAsync();
        fixture.Reader.Value = fixture.Context(parent: fixture.Parent, travelerName: "Renamed Account");
        var expected = (await fixture.ReloadAsync()).PublicDisplayName;

        var result = await fixture.HandleAsync(Command(publishDisplayName: preference));

        result.IsSuccess.Should().BeTrue();
        (await fixture.ReloadAsync()).PublicDisplayName.Should().Be(expected);
    }

    [Fact]
    public async Task BlankCurrentName_WhenPreferenceChanges_UsesNeutralFallback()
    {
        await using var fixture = await Fixture.CreateAsync(profileName: "Original Name");
        fixture.Db.Users.Single().FullName = " ";
        await fixture.Db.SaveChangesAsync();
        fixture.Reader.Value = fixture.Context(parent: fixture.Parent, travelerName: " ");

        var result = await fixture.HandleAsync(Command(publishDisplayName: true));

        result.IsSuccess.Should().BeTrue();
        (await fixture.ReloadAsync()).PublicDisplayName.Should().Be(TripReview.NeutralDisplayName);
    }

    [Fact]
    public async Task SuccessfulEdit_PreservesSubjectC4CreationAndDeadline()
    {
        await using var fixture = await Fixture.CreateAsync(cspRating: 4);

        var result = await fixture.HandleAsync(Command());

        result.IsSuccess.Should().BeTrue();
        var saved = await fixture.ReloadAsync();
        saved.BookingId.Should().Be(10);
        saved.TravelerUserId.Should().Be(1);
        saved.TourId.Should().BeNull();
        saved.ItineraryId.Should().Be(20);
        saved.RoutePacing.Should().Be(RoutePacingFeedback.WellPaced);
        saved.CspRating.Should().Be(4);
        saved.CreatedAtUtc.Should().Be(Created);
        saved.EditDeadlineUtc.Should().Be(Created.AddDays(7));
    }

    [Fact]
    public async Task FinalLockedAccountChange_IsRejectedAndPreservesPublication()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Values.Enqueue(fixture.Context(parent: fixture.Parent));
        fixture.Reader.Values.Enqueue(fixture.Context(parent: fixture.Parent, travelerStatus: AccountStatus.Locked));

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.Forbidden);
        fixture.WriteLock.Calls.Should().Be(1);
        await fixture.AssertOriginalAsync();
    }

    [Fact]
    public async Task FinalBusinessConflictAfterEarlyRead_IsRejectedWithoutMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Reader.Values.Enqueue(fixture.Context(parent: fixture.Parent));
        fixture.Reader.Values.Enqueue(fixture.Context(parent: fixture.Parent, status: "Confirmed"));

        var result = await fixture.HandleAsync(Command());

        result.ErrorCode.Should().Be(TripReviewErrorCodes.BookingNotCompleted);
        await fixture.AssertOriginalAsync();
    }

    private static EditTripReviewCommand Command(int rating = 5, string title = "New Title",
        string content = "New Content", bool publishDisplayName = false, string? version = null) =>
        new(10, rating, title, content, publishDisplayName,
            version ?? Convert.ToBase64String(OriginalVersion));

    private static LegacyTripReviewEntryDto Legacy() =>
        new(90, Review.TargetTypePoi, 5, 4, "legacy", Created);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _originalTitle = "Old Title";
        private readonly string _originalContent = "Old Content";

        private Fixture(TestDbContext db, FakeCurrentUserService user, FakeDateTimeProvider clock,
            TripReview parent, string profileName)
        {
            Db = db;
            Parent = parent;
            Clock = clock;
            Reader = new Reader { Value = Context(parent: parent, travelerName: profileName) };
            WriteLock = new Lock();
            Moderator = new Moderator();
            Handler = new EditTripReviewCommandHandler(db, user, clock, Reader, WriteLock, Moderator);
            Db.OnTransactionCompleted = () => WriteLock.IsHeld = false;
        }

        public TestDbContext Db { get; }
        public TripReview Parent { get; }
        public FakeDateTimeProvider Clock { get; }
        public Reader Reader { get; }
        public Lock WriteLock { get; }
        public Moderator Moderator { get; }
        public EditTripReviewCommandHandler Handler { get; }

        public static async Task<Fixture> CreateAsync(UserRole role = UserRole.Traveler,
            AccountStatus status = AccountStatus.Active, string claim = nameof(UserRole.Traveler),
            long? userId = 1, bool addSecondTraveler = false,
            DateTimeOffset? now = null, bool originalPublishDisplayName = false,
            string profileName = "Owner", string? policyVersion = null, byte? cspRating = null)
        {
            var db = TestDbContext.Create();
            db.Users.Add(new User { Id = 1, Role = role, Status = status, FullName = profileName });
            if (addSecondTraveler)
                db.Users.Add(new User { Id = 2, Role = UserRole.Traveler, Status = AccountStatus.Active, FullName = "Other" });
            var parent = TripReview.CreatePublished(10, 1, null, 20, 4,
                "Old Title", "Old Content", RoutePacingFeedback.WellPaced, cspRating,
                originalPublishDisplayName, profileName, policyVersion, Created);
            SetVersion(parent, OriginalVersion);
            db.TripReviews.Add(parent);
            await db.SaveChangesAsync();
            return new Fixture(db,
                new FakeCurrentUserService { UserId = userId, Role = claim },
                new FakeDateTimeProvider { UtcNow = now ?? BeforeDeadline }, parent, profileName);
        }

        public Task<Result<NewTripReviewDto>> HandleAsync(EditTripReviewCommand command) =>
            Handler.Handle(command, default);

        public TripReviewContextData Context(TripReview? parent = null,
            IReadOnlyList<LegacyTripReviewEntryDto>? legacy = null,
            string status = TripReviewContextValues.Completed,
            AccountStatus travelerStatus = AccountStatus.Active,
            string travelerName = "Owner") =>
            new(new TripReviewBookingData
            {
                BookingId = 10,
                BookingStatus = status,
                ItineraryId = 20,
                ItineraryOwnerId = 1,
                ItinerarySourceType = Itinerary.ManualSourceType,
                TravelerRole = nameof(UserRole.Traveler),
                TravelerStatus = travelerStatus.ToString(),
                TravelerFullName = travelerName,
            }, parent, legacy ?? [], false, false);

        public async Task<TripReview> ReloadAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.TripReviews.SingleAsync();
        }

        public async Task AssertOriginalAsync()
        {
            var saved = await ReloadAsync();
            saved.OverallRating.Should().Be(4);
            saved.Title.Should().Be(_originalTitle);
            saved.Content.Should().Be(_originalContent);
            saved.UpdatedAtUtc.Should().Be(Created);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static void SetVersion(TripReview review, byte[] version) =>
            typeof(TripReview).GetProperty(nameof(TripReview.Version))!.SetValue(review, version.ToArray());
    }

    private sealed class Reader : ITripReviewContextReader
    {
        public TripReviewContextData? Value { get; set; }
        public Queue<TripReviewContextData?> Values { get; } = new();
        public int Calls { get; private set; }

        public Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken)
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

    private sealed class Moderator : IReviewContentModerator
    {
        public string ActivePolicyVersion => ReviewContentPolicy.ActiveVersion;
        public int Calls { get; private set; }
        public ReviewContentModerationResult Next { get; set; } =
            ReviewContentModerationResult.Accepted(ReviewContentPolicy.ActiveVersion);

        public Task<ReviewContentModerationResult> ScreenAsync(
            ReviewText text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Next);
        }
    }
}
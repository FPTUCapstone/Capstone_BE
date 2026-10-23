using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.GetContext;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripReviews;

public sealed class GetTripReviewContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    private static TripReviewBookingData Booking() => new()
    {
        BookingId = 10,
        BookingStatus = TripReviewContextValues.Completed,
        ItineraryId = 20,
        ItineraryOwnerId = 1,
        ItinerarySourceType = Itinerary.ManualSourceType,
    };

    private static TripReview Parent() => TripReview.CreatePublished(10, 1, null, 20, 4,
        "Title", "Content", null, 2, false, "Old Name", "p1", Now.AddDays(-1));

    private static async Task<TestDbContext> DatabaseAsync(UserRole role = UserRole.Traveler, AccountStatus status = AccountStatus.Active)
    {
        var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Users.Add(new User { Id = 1, Role = role, Status = status, FullName = "Nguyễn Tiến Đạt", Email = "private@test.invalid" });
        await db.SaveChangesAsync(default);
        return db;
    }

    private static GetTripReviewContextQueryHandler Handler(TestDbContext db, Reader reader, long? userId = 1, string? role = nameof(UserRole.Traveler), DateTimeOffset? now = null) =>
        new(db, new FakeCurrentUserService { UserId = userId, Role = role },
            new FakeDateTimeProvider { UtcNow = now ?? Now }, reader);

    [Fact]
    public async Task Unauthenticated_DoesNotReadAnyBooking()
    {
        await using var db = await DatabaseAsync();
        var reader = new Reader();
        var result = await Handler(db, reader, userId: null).Handle(new(10), default);
        result.ErrorCode.Should().Be(TripReviewErrorCodes.Unauthorized);
        reader.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(UserRole.Administrator, AccountStatus.Active, "Traveler")]
    [InlineData(UserRole.Traveler, AccountStatus.Locked, "Traveler")]
    [InlineData(UserRole.Traveler, AccountStatus.Inactive, "Traveler")]
    [InlineData(UserRole.Traveler, AccountStatus.PendingEmailVerification, "Traveler")]
    [InlineData(UserRole.Traveler, AccountStatus.Active, "Administrator")]
    public async Task WrongRoleOrInactive_DoesNotReadBooking(UserRole role, AccountStatus status, string claim)
    {
        await using var db = await DatabaseAsync(role, status);
        var reader = new Reader();
        var result = await Handler(db, reader, role: claim).Handle(new(10), default);
        result.ErrorCode.Should().Be(TripReviewErrorCodes.Forbidden);
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task MissingOrForeignBooking_ReturnsSameNotFound()
    {
        await using var db = await DatabaseAsync();
        var reader = new Reader { Value = null };
        var result = await Handler(db, reader).Handle(new(123), default);
        result.ErrorCode.Should().Be(TripReviewErrorCodes.BookingNotFound);
        reader.RequestedBooking.Should().Be(123);
        reader.RequestedOwner.Should().Be(1);
    }

    [Fact]
    public async Task CompletedOwnedItinerary_ReturnsOverallButUnavailableHistoricalCapabilities()
    {
        await using var db = await DatabaseAsync();
        var result = await Handler(db, new Reader()).Handle(new(10), default);
        result.IsSuccess.Should().BeTrue();
        var value = result.Value;
        value.CanSubmit.Should().BeTrue();
        value.SubmitUnavailableReason.Should().BeNull();
        value.Subject.Should().Be(new TripReviewSubjectDto(TripReviewContextValues.ItinerarySubject, null, 20));
        value.ExistingReviewKind.Should().Be(TripReviewContextValues.None);
        value.Review.Should().BeNull();
        value.RoutePacing.Reason.Should().Be(TripReviewContextValues.RouteContextUnavailable);
        value.PoiRatings.Reason.Should().Be(TripReviewContextValues.VisitEvidenceUnavailable);
        value.EligiblePois.Should().BeEmpty();
        value.CspRating.Available.Should().BeFalse();
        value.DisplayNamePreview.Initials.Should().Be("N. T. Đ.");
        value.DisplayNamePreview.FullNameWithConsent.Should().Be("Nguyễn Tiến Đạt");
        db.ChangeTracker.Entries<TripReview>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("PendingPayment")]
    [InlineData("Confirmed")]
    [InlineData("Cancelled")]
    [InlineData("Expired")]
    public async Task NonCompletedBooking_DisablesSubmissionAndAllCapabilities(string status)
    {
        await using var db = await DatabaseAsync();
        var reader = new Reader { Value = new(Booking() with { BookingStatus = status }, null, [], false, false) };
        var value = (await Handler(db, reader).Handle(new(10), default)).Value;
        value.CanSubmit.Should().BeFalse();
        value.SubmitUnavailableReason.Should().Be(TripReviewContextValues.BookingNotCompleted);
        value.RoutePacing.Reason.Should().Be(TripReviewContextValues.BookingNotCompleted);
        value.CspRating.Reason.Should().Be(TripReviewContextValues.BookingNotCompleted);
        value.PoiRatings.Reason.Should().Be(TripReviewContextValues.BookingNotCompleted);
    }

    [Fact]
    public async Task ScheduleOnly_UsesTourSubjectButNotMutableTourAsRouteEvidence()
    {
        await using var db = await DatabaseAsync();
        var booking = Booking() with { TourScheduleId = 30, TourId = 40, ItineraryId = null, ItineraryOwnerId = null };
        var value = (await Handler(db, new Reader { Value = new(booking, null, [], false, false) }).Handle(new(10), default)).Value;
        value.Subject.Should().Be(new TripReviewSubjectDto(TripReviewContextValues.TourSubject, 40, null));
        value.CanSubmit.Should().BeTrue();
        value.RoutePacing.Available.Should().BeFalse();
    }

    [Theory]
    [InlineData(1L, true)]
    [InlineData(2L, false)]
    [InlineData(null, false)]
    public async Task CspRequiresOwnedSchedulingRequest_IndependentOfVisitedOrPacing(long? requestOwner, bool allowed)
    {
        await using var db = await DatabaseAsync();
        var booking = Booking() with { ItinerarySourceType = Itinerary.CspGeneratedSourceType, SchedulingRequestId = 50, SchedulingRequestOwnerId = requestOwner };
        var value = (await Handler(db, new Reader { Value = new(booking, null, [], false, false) }).Handle(new(10), default)).Value;
        value.CspRating.Available.Should().Be(allowed);
        value.CspRating.Reason.Should().Be(allowed ? null : TripReviewContextValues.CspProvenanceUnavailable);
        value.RoutePacing.Available.Should().BeFalse();
        value.PoiRatings.Available.Should().BeFalse();
    }

    [Theory]
    [InlineData("foreignItinerary")]
    [InlineData("missingItinerary")]
    [InlineData("missingTour")]
    [InlineData("noSubject")]
    [InlineData("mismatchedBookedTour")]
    public async Task BrokenOrForeignLinks_ReturnInconsistentWithoutForeignSubject(string scenario)
    {
        await using var db = await DatabaseAsync();
        var booking = scenario switch
        {
            "foreignItinerary" => Booking() with { ItineraryOwnerId = 2 },
            "missingItinerary" => Booking() with { ItineraryOwnerId = null },
            "missingTour" => Booking() with { TourScheduleId = 30, TourId = null },
            "noSubject" => Booking() with { ItineraryId = null },
            _ => Booking() with { TourScheduleId = 30, TourId = 40, ItinerarySourceType = Itinerary.BookedTourSourceType, SourceTourId = 41 },
        };
        var value = (await Handler(db, new Reader { Value = new(booking, null, [], false, false) }).Handle(new(10), default)).Value;
        value.Subject.Should().BeNull();
        value.CanSubmit.Should().BeFalse();
        value.SubmitUnavailableReason.Should().Be(TripReviewContextValues.InconsistentContext);
        value.RoutePacing.Reason.Should().Be(TripReviewContextValues.InconsistentContext);
        value.CspRating.Reason.Should().Be(TripReviewContextValues.InconsistentContext);
        value.PoiRatings.Reason.Should().Be(TripReviewContextValues.InconsistentContext);
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public async Task ExistingParent_RemainsReadableAcrossExclusiveDeadline(long ticks, int editableCount)
    {
        await using var db = await DatabaseAsync();
        var parent = Parent();
        var reader = new Reader { Value = new(Booking(), parent, [], false, false) };
        var value = (await Handler(db, reader, now: parent.EditDeadlineUtc.AddTicks(ticks)).Handle(new(10), default)).Value;
        value.ExistingReviewKind.Should().Be(TripReviewContextValues.New);
        value.CanSubmit.Should().BeFalse();
        value.SubmitUnavailableReason.Should().Be(TripReviewContextValues.AlreadyReviewed);
        value.EditableFields.Should().HaveCount(editableCount);
        var dto = value.Review.Should().BeOfType<NewTripReviewDto>().Subject;
        dto.PublicDisplayName.Should().Be("O. N.");
        dto.EditDeadlineUtc.Should().Be(parent.EditDeadlineUtc);
        dto.CspRating.Should().Be(2);
        dto.Version.Should().Be(Convert.ToBase64String(parent.Version));
        // Full API transport is Task 12; the read DTO nevertheless must not expose private fields.
        JsonSerializer.Serialize(value).Should().NotContain("private@test.invalid").And.NotContain("PolicyVersion");
    }

    [Fact]
    public async Task OwnedLegacy_BlocksCreateWithoutInventedTitlePolicyOrVersion()
    {
        await using var db = await DatabaseAsync();
        var entry = new LegacyTripReviewEntryDto(99, Review.TargetTypePoi, 77, 3, "Legacy", Now);
        var value = (await Handler(db, new Reader { Value = new(Booking(), null, [entry], false, false) }).Handle(new(10), default)).Value;
        value.ExistingReviewKind.Should().Be(TripReviewContextValues.Legacy);
        value.SubmitUnavailableReason.Should().Be(TripReviewContextValues.AlreadyReviewed);
        value.Review.Should().BeOfType<LegacyTripReviewDto>().Which.Entries.Should().Equal(entry);
        JsonSerializer.Serialize(value.Review).Should().NotContain("EditDeadline").And.NotContain("Version").And.NotContain("Title");
    }

    [Fact]
    public async Task ForeignReviewMetadata_DisablesCreateButNeverLeaksReviewContent()
    {
        await using var db = await DatabaseAsync();
        var value = (await Handler(db, new Reader { Value = new(Booking(), null, [], false, true) }).Handle(new(10), default)).Value;
        value.CanSubmit.Should().BeFalse();
        value.SubmitUnavailableReason.Should().Be(TripReviewContextValues.InconsistentContext);
        value.Review.Should().BeNull();
    }

    [Fact]
    public async Task ForeignLegacyOnly_ReturnsConsistentLegacyDiscriminatorWithoutForeignEntries()
    {
        await using var db = await DatabaseAsync();
        var value = (await Handler(db, new Reader { Value = new(Booking(), null, [], true, false) }).Handle(new(10), default)).Value;
        value.ExistingReviewKind.Should().Be(TripReviewContextValues.Legacy);
        value.SubmitUnavailableReason.Should().Be(TripReviewContextValues.LegacyConflict);
        value.Review.Should().BeOfType<LegacyTripReviewDto>().Which.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ExistingParentWithDifferentSubject_IsReadableButContextDoesNotAuthorizeEdit()
    {
        await using var db = await DatabaseAsync();
        var booking = Booking() with { ItineraryId = 21 };
        var value = (await Handler(db, new Reader { Value = new(booking, Parent(), [], false, false) }).Handle(new(10), default)).Value;
        value.SubmitUnavailableReason.Should().Be(TripReviewContextValues.InconsistentContext);
        value.Subject.Should().BeNull();
        value.EditableFields.Should().BeEmpty();
        value.Review.Should().BeOfType<NewTripReviewDto>().Which.Subject.ItineraryId.Should().Be(20);
    }

    [Fact]
    public async Task NewAndLegacySameBookingOverlap_IsExplicitCompatibilityConflict()
    {
        await using var db = await DatabaseAsync();
        var entry = new LegacyTripReviewEntryDto(99, Review.TargetTypePoi, 77, 3, "Legacy", Now);
        var value = (await Handler(db, new Reader { Value = new(Booking(), Parent(), [entry], false, false) }).Handle(new(10), default)).Value;
        value.SubmitUnavailableReason.Should().Be(TripReviewContextValues.LegacyConflict);
        value.EditableFields.Should().BeEmpty();
    }

    private sealed class Reader : ITripReviewContextReader
    {
        public TripReviewContextData? Value { get; set; } = new(Booking(), null, [], false, false);
        public int Calls { get; private set; }
        public long RequestedBooking { get; private set; }
        public long RequestedOwner { get; private set; }
        public Task<TripReviewContextData?> ReadOwnedAsync(long bookingId, long travelerUserId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            RequestedBooking = bookingId;
            RequestedOwner = travelerUserId;
            return Task.FromResult(Value);
        }

        public Task<TripReviewContextData?> ReadOwnedForUpdateAsync(long bookingId, long travelerUserId,
            CancellationToken cancellationToken) => ReadOwnedAsync(bookingId, travelerUserId, cancellationToken);
    }
}
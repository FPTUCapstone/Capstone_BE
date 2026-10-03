using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TripReviewTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 28, 15, 0, 0, TimeSpan.FromHours(7));

    private static TripReview Create(byte rating = 5, string title = "Title", string content = "Content",
        RoutePacingFeedback? pacing = null, byte? csp = null, bool consent = false,
        string? name = "Nguyễn Tiến Đạt", long booking = 1, long author = 1,
        long? tour = null, long? itinerary = 1, string policy = "approved-test-policy") =>
        TripReview.CreatePublished(booking, author, tour, itinerary, rating, title, content,
            pacing, csp, consent, name, policy, Created);

    [Fact]
    public void Create_PreservesIndependentScoresAndOriginalUtcDeadline()
    {
        var review = Create(pacing: RoutePacingFeedback.TooTight, csp: 2);
        review.Id.Should().Be(0);
        review.BookingId.Should().Be(1);
        review.TravelerUserId.Should().Be(1);
        review.TourId.Should().BeNull();
        review.ItineraryId.Should().Be(1);
        review.OverallRating.Should().Be(5);
        review.RoutePacing.Should().Be(RoutePacingFeedback.TooTight);
        review.CspRating.Should().Be(2);
        review.CreatedAtUtc.Should().Be(Created.ToUniversalTime());
        review.CreatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        review.EditDeadlineUtc.Should().Be(Created.AddDays(7));
        review.UpdatedAtUtc.Should().Be(review.CreatedAtUtc);
        review.PublicationStatus.Should().Be(TripReview.PublishedStatus);
        review.PolicyVersion.Should().Be("approved-test-policy");
        review.Version.Should().BeEmpty();
    }

    [Fact]
    public void Create_OmittedC4HasNoDefaultSelection()
    {
        var review = Create();
        review.RoutePacing.Should().BeNull();
        review.CspRating.Should().BeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Create_AcceptsRatingBoundaries(byte rating) =>
        Create(rating: rating, csp: rating).OverallRating.Should().Be(rating);

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Create_RejectsInvalidOverall(byte rating)
    {
        Action action = () => Create(rating: rating);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Create_RejectsInvalidCsp(byte rating)
    {
        Action action = () => Create(csp: rating);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_RejectsUndefinedPacing()
    {
        Action action = () => Create(pacing: (RoutePacingFeedback)99);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0L, 1L, null, 1L)]
    [InlineData(1L, 0L, null, 1L)]
    [InlineData(1L, 1L, null, null)]
    [InlineData(1L, 1L, 1L, 1L)]
    [InlineData(1L, 1L, 0L, null)]
    [InlineData(1L, 1L, null, -1L)]
    public void Create_RejectsInvalidIdentity(long booking, long author, long? tour, long? itinerary)
    {
        Action action = () => Create(booking: booking, author: author, tour: tour, itinerary: itinerary);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_TourSubjectIsSeparateFromItinerarySubject()
    {
        var review = Create(tour: 10, itinerary: null);
        review.TourId.Should().Be(10);
        review.ItineraryId.Should().BeNull();
    }

    [Theory]
    [InlineData("", "Content")]
    [InlineData("Title", " ")]
    [InlineData(" Title ", "Content")]
    [InlineData("Title", " Content ")]
    public void Create_RejectsUnnormalizedOrEmptyModeratedText(string title, string content)
    {
        Action action = () => Create(title: title, content: content);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_EnforcesUtf16BoundsWithoutPostModerationTransformation()
    {
        Create(title: new string('a', 200), content: new string('b', 1000)).Title.Length.Should().Be(200);
        Action title = () => Create(title: new string('a', 201));
        Action content = () => Create(content: new string('b', 1001));
        title.Should().Throw<ArgumentException>();
        content.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_RequiresAcceptedPolicyVersion(string policy)
    {
        Action action = () => Create(policy: policy);
        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Nguyễn Tiến Đạt", "N. T. Đ.")]
    [InlineData("  Nguyễn\tTiến\nĐạt  ", "N. T. Đ.")]
    [InlineData("A\u0301nh 😀 Trip", "A\u0301. 😀. T.")]
    [InlineData(" ", "Traveler")]
    [InlineData(null, "Traveler")]
    public void Create_WithoutConsentStoresOnlyUnicodeInitials(string? name, string expected) =>
        Create(name: name).PublicDisplayName.Should().Be(expected);

    [Theory]
    [InlineData(false, "N. T. Đ.")]
    [InlineData(true, "Nguyễn Tiến Đạt")]
    public void Edit_SamePreferencePreservesSnapshotAndImmutableFields(bool consent, string snapshot)
    {
        var review = Create(pacing: RoutePacingFeedback.WellPaced, csp: 3, consent: consent);
        review.TryEditPublished(1, "Edited", "Exact screened content", consent, "Different Profile",
            "policy-v2", Created.AddDays(1)).Should().BeTrue();
        review.PublicDisplayName.Should().Be(snapshot);
        review.Title.Should().Be("Edited");
        review.Content.Should().Be("Exact screened content");
        review.OverallRating.Should().Be(1);
        review.PolicyVersion.Should().Be("policy-v2");
        review.EditDeadlineUtc.Should().Be(Created.AddDays(7));
        review.CreatedAtUtc.Should().Be(Created);
        review.RoutePacing.Should().Be(RoutePacingFeedback.WellPaced);
        review.CspRating.Should().Be(3);
        review.UpdatedAtUtc.Should().Be(Created.AddDays(1));
        review.UpdatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Edit_ChangedPreferenceUsesCurrentProfileSnapshot()
    {
        var review = Create(consent: true);
        review.PublicDisplayName.Should().Be("Nguyễn Tiến Đạt");
        review.TryEditPublished(4, "Title", "Content", false, "Mai Dat", "p2", Created.AddDays(1));
        review.PublicDisplayName.Should().Be("M. D.");
        review.PublishDisplayName.Should().BeFalse();
        review.TryEditPublished(4, "Title", "Content", true, "New Profile", "p3", Created.AddDays(2));
        review.PublicDisplayName.Should().Be("New Profile");
        review.PublishDisplayName.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void Edit_UsesExclusiveOriginalDeadline(long ticks, bool allowed)
    {
        var review = Create();
        review.TryEditPublished(1, "Edited", "Edited", true, "Changed", "p2",
            Created.AddDays(7).AddTicks(ticks)).Should().Be(allowed);
        review.EditDeadlineUtc.Should().Be(Created.AddDays(7));
        if (!allowed)
        {
            review.Title.Should().Be("Title");
            review.OverallRating.Should().Be(5);
            review.PublicDisplayName.Should().Be("N. T. Đ.");
            review.PolicyVersion.Should().Be("approved-test-policy");
            review.UpdatedAtUtc.Should().Be(Created);
        }
    }

    [Fact]
    public void Edit_InvalidNormalizedPayloadDoesNotPartiallyMutate()
    {
        var review = Create();
        Action action = () => review.TryEditPublished(1, "Edited", "  ", true,
            "New Profile", "p2", Created.AddDays(1));
        action.Should().Throw<ArgumentException>();
        review.Title.Should().Be("Title");
        review.OverallRating.Should().Be(5);
        review.PublishDisplayName.Should().BeFalse();
        review.PublicDisplayName.Should().Be("N. T. Đ.");
    }
}
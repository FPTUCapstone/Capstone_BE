using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public class ReviewTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Tour")]
    [InlineData("POI")]
    [InlineData("RouteSegment")]
    [InlineData("Operator")]
    public void Create_WithAllowedTargetTypes_Succeeds(string targetType)
    {
        var review = Review.Create(
            travelerUserId: 10,
            targetType: targetType,
            targetId: 100,
            rating: 5,
            createdAtUtc: Now);

        review.TravelerUserId.Should().Be(10);
        review.TargetType.Should().Be(targetType);
        review.TargetId.Should().Be(100);
        review.Rating.Should().Be(5);
        review.CreatedAtUtc.Should().Be(Now);
        review.ScenicRating.Should().BeNull();
        review.PhotoRating.Should().BeNull();
        review.Comment.Should().BeNull();
        review.BookingId.Should().BeNull();
    }

    [Fact]
    public void Create_WithPaddedTargetType_TrimsWhitespace()
    {
        var review = Review.Create(
            travelerUserId: 10,
            targetType: "  POI  ",
            targetId: 100,
            rating: 4,
            createdAtUtc: Now);

        review.TargetType.Should().Be("POI");
    }

    [Theory]
    [InlineData("Invalid")]
    [InlineData("Hotel")]
    [InlineData("User")]
    [InlineData("poi")]
    [InlineData("tour")]
    public void Create_WithUnapprovedTargetType_ThrowsArgumentException(string invalidTargetType)
    {
        var action = () => Review.Create(
            travelerUserId: 10,
            targetType: invalidTargetType,
            targetId: 100,
            rating: 5,
            createdAtUtc: Now);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*target type*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithNullOrWhitespaceTargetType_ThrowsArgumentException(string? invalidTargetType)
    {
        var action = () => Review.Create(
            travelerUserId: 10,
            targetType: invalidTargetType!,
            targetId: 100,
            rating: 5,
            createdAtUtc: Now);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreatePoiReview_ConstructsValidPoiReview()
    {
        var review = Review.CreatePoiReview(
            travelerUserId: 15,
            poiId: 42,
            rating: 5,
            createdAtUtc: Now,
            scenicRating: 4,
            photoRating: 5,
            comment: "Scenic landmark with great photo spots.");

        review.TravelerUserId.Should().Be(15);
        review.TargetType.Should().Be(Review.TargetTypePoi);
        review.TargetId.Should().Be(42);
        review.Rating.Should().Be(5);
        review.ScenicRating.Should().Be(4);
        review.PhotoRating.Should().Be(5);
        review.Comment.Should().Be("Scenic landmark with great photo spots.");
        review.BookingId.Should().BeNull();
        review.CreatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_WithNonPoiTargetTypeAndScenicRating_ThrowsArgumentException()
    {
        var action = () => Review.Create(
            travelerUserId: 10,
            targetType: Review.TargetTypeTour,
            targetId: 100,
            rating: 4,
            createdAtUtc: Now,
            scenicRating: 4);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*only supported for POI*");
    }

    [Fact]
    public void Create_WithNonPoiTargetTypeAndPhotoRating_ThrowsArgumentException()
    {
        var action = () => Review.Create(
            travelerUserId: 10,
            targetType: Review.TargetTypeTour,
            targetId: 100,
            rating: 4,
            createdAtUtc: Now,
            photoRating: 4);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*only supported for POI*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Create_WithRatingOutOfRange_ThrowsArgumentOutOfRangeException(byte invalidRating)
    {
        var action = () => Review.Create(
            travelerUserId: 10,
            targetType: Review.TargetTypePoi,
            targetId: 100,
            rating: invalidRating,
            createdAtUtc: Now);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Rating must be between 1 and 5*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Create_WithScenicRatingOutOfRange_ThrowsArgumentOutOfRangeException(byte invalidRating)
    {
        var action = () => Review.Create(
            travelerUserId: 10,
            targetType: Review.TargetTypePoi,
            targetId: 100,
            rating: 5,
            createdAtUtc: Now,
            scenicRating: invalidRating);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Scenic rating must be between 1 and 5*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Create_WithPhotoRatingOutOfRange_ThrowsArgumentOutOfRangeException(byte invalidRating)
    {
        var action = () => Review.Create(
            travelerUserId: 10,
            targetType: Review.TargetTypePoi,
            targetId: 100,
            rating: 5,
            createdAtUtc: Now,
            photoRating: invalidRating);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Photo rating must be between 1 and 5*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithInvalidTravelerUserId_ThrowsArgumentOutOfRangeException(long invalidUserId)
    {
        var action = () => Review.Create(
            travelerUserId: invalidUserId,
            targetType: Review.TargetTypePoi,
            targetId: 100,
            rating: 5,
            createdAtUtc: Now);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithInvalidTargetId_ThrowsArgumentOutOfRangeException(long invalidTargetId)
    {
        var action = () => Review.Create(
            travelerUserId: 10,
            targetType: Review.TargetTypePoi,
            targetId: invalidTargetId,
            rating: 5,
            createdAtUtc: Now);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithCommentExceedingMaxLength_ThrowsArgumentException()
    {
        var tooLongComment = new string('x', Review.CommentMaxLength + 1);

        var action = () => Review.Create(
            travelerUserId: 10,
            targetType: Review.TargetTypePoi,
            targetId: 100,
            rating: 5,
            createdAtUtc: Now,
            comment: tooLongComment);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Comment cannot exceed*");
    }

    [Fact]
    public void Create_WithCommentAtMaxLength_Succeeds()
    {
        var maxLengthComment = new string('x', Review.CommentMaxLength);

        var review = Review.Create(
            travelerUserId: 10,
            targetType: Review.TargetTypePoi,
            targetId: 100,
            rating: 5,
            createdAtUtc: Now,
            comment: maxLengthComment);

        review.Comment.Should().Be(maxLengthComment);
    }
}
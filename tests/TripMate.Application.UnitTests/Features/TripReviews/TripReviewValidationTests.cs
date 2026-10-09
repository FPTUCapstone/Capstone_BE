using FluentAssertions;

using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Edit;
using TripMate.Application.Features.TripReviews.Submit;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.TripReviews;

public sealed class TripReviewValidationTests
{
    private static SubmitTripReviewCommand Submit(int rating = 5, string? title = "Title", string? content = "Content",
        IReadOnlyList<TripReviewPoiInput>? pois = null, IReadOnlyList<TripReviewPhotoInput>? photos = null,
        RoutePacingFeedback? pacing = null, int? csp = null) =>
        new(1, rating, title, content, pois ?? [], photos ?? [], pacing, csp);

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(6, false)]
    [InlineData(-1, false)]
    public void OverallRating_BoundsApplyToCreateAndEdit(int rating, bool valid)
    {
        new SubmitTripReviewCommandValidator().Validate(Submit(rating)).IsValid.Should().Be(valid);
        new EditTripReviewCommandValidator().Validate(new EditTripReviewCommand(1, rating, "Title", "Content", false, "AQIDBAUGBwg=")).IsValid.Should().Be(valid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n\u2003 ")]
    public void EmptyNormalizedText_IsInvalidForBothCommands(string? text)
    {
        new SubmitTripReviewCommandValidator().Validate(Submit(title: text, content: text)).IsValid.Should().BeFalse();
        new EditTripReviewCommandValidator().Validate(new EditTripReviewCommand(1, 5, text, text, false, "AQIDBAUGBwg=")).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(200, 1000, true)]
    [InlineData(201, 1000, false)]
    [InlineData(200, 1001, false)]
    public void Length_IsMeasuredAfterTrimInUtf16(int titleLength, int contentLength, bool valid)
    {
        var title = "  " + new string('x', titleLength) + "  ";
        var content = "\t" + new string('y', contentLength) + "\n";
        var command = Submit(title: title, content: content);
        command.Text.Title.Length.Should().Be(titleLength);
        command.Text.Content.Length.Should().Be(contentLength);
        new SubmitTripReviewCommandValidator().Validate(command).IsValid.Should().Be(valid);
        new EditTripReviewCommandValidator().Validate(new EditTripReviewCommand(1, 1, title, content, false, "AQIDBAUGBwg=")).IsValid.Should().Be(valid);
    }

    [Fact]
    public void Normalization_DoesNotRewriteInternalWhitespaceOrUnicode()
    {
        var command = Submit(title: "  e\u0301  👩‍💻  ", content: "  A\n B  ");
        command.Text.Title.Should().Be("e\u0301  👩‍💻");
        command.Text.Content.Should().Be("A\n B");
        new SubmitTripReviewCommandValidator().Validate(Submit(title: string.Concat(Enumerable.Repeat("😀", 101)))).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(6, false)]
    public void CspRating_IsNullableAndHasIndependentBounds(int? rating, bool valid) =>
        new SubmitTripReviewCommandValidator().Validate(Submit(csp: rating)).IsValid.Should().Be(valid);

    [Theory]
    [InlineData(null, true)]
    [InlineData(RoutePacingFeedback.TooTight, true)]
    [InlineData(RoutePacingFeedback.WellPaced, true)]
    [InlineData(RoutePacingFeedback.TooLoose, true)]
    [InlineData((RoutePacingFeedback)0, false)]
    [InlineData((RoutePacingFeedback)4, false)]
    public void RoutePacing_AcceptsOnlyDefinedValues(RoutePacingFeedback? pacing, bool valid) =>
        new SubmitTripReviewCommandValidator().Validate(Submit(pacing: pacing)).IsValid.Should().Be(valid);

    [Fact]
    public void PoiRatings_RequireDistinctPositiveIdsAndValidRatings()
    {
        var validator = new SubmitTripReviewCommandValidator();
        validator.Validate(Submit(pois: [new(1, 1), new(2, 5)])).IsValid.Should().BeTrue();
        foreach (var inputs in new TripReviewPoiInput[][] { [new(1, 1), new(1, 5)], [new(0, 3)], [new(1, 0)], [new(1, 6)], [null!] })
            validator.Validate(Submit(pois: inputs)).IsValid.Should().BeFalse();
        validator.Validate(new SubmitTripReviewCommand(1, 5, "Title", "Content", null, [])).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Photos_CountBoundary(int count, bool valid) =>
        new SubmitTripReviewCommandValidator().Validate(Submit(photos: Enumerable.Range(0, count)
            .Select(_ => new TripReviewPhotoInput("photo.jpg", "image/jpeg", 1, new byte[1])).ToArray())).IsValid.Should().Be(valid);

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(5000000, 5000000, true)]
    [InlineData(5000001, 1, false)]
    [InlineData(1, 5000001, false)]
    [InlineData(0, 0, false)]
    [InlineData(-1, 1, false)]
    public void Photos_ValidateDeclaredAndActualRawLength(long declared, int actual, bool valid) =>
        new SubmitTripReviewCommandValidator().Validate(Submit(photos: [new("photo.jpg", "image/jpeg", declared, new byte[actual])])).IsValid.Should().Be(valid);

    [Theory]
    [InlineData("a.jpg", "image/jpeg", true)]
    [InlineData("a.jpeg", "image/jpeg", true)]
    [InlineData("a.png", "image/png", true)]
    [InlineData("a.webp", "image/webp", true)]
    [InlineData("a.gif", "image/gif", false)]
    [InlineData("a.svg", "image/svg+xml", false)]
    [InlineData("a.png", "image/jpeg", false)]
    [InlineData("https://res.cloudinary.com/cloud/image/upload/a.jpg", "image/jpeg", false)]
    [InlineData("", "image/jpeg", false)]
    public void PhotoMetadata_IsLocalSupportedFormatNotProviderUrl(string fileName, string contentType, bool valid) =>
        new SubmitTripReviewCommandValidator().Validate(Submit(photos: [new(fileName, contentType, 1, new byte[1])])).IsValid.Should().Be(valid);

    [Theory]
    [InlineData("AQIDBAUGBwg=", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("AQIDBAUGBwg", false)]
    [InlineData(" AQIDBAUGBwg=", false)]
    [InlineData("AQID", false)]
    [InlineData("AQIDBAUGBwh=", false)]
    [InlineData("W/\"AQIDBAUGBwg=\"", false)]
    public void EditVersion_MustBeCanonicalEightByteBase64(string? version, bool valid) =>
        new EditTripReviewCommandValidator().Validate(new EditTripReviewCommand(1, 5, "Title", "Content", false, version)).IsValid.Should().Be(valid);

    [Fact]
    public void Commands_DoNotAcceptAuthorProviderIdentityOrImmutableEditFields()
    {
        var submit = typeof(SubmitTripReviewCommand).GetProperties().Select(p => p.Name);
        submit.Should().NotContain(["AuthorId", "TravelerUserId", "DeliveryUrl", "PublicId", "CanRateCsp", "EligiblePoiIds"]);
        var edit = typeof(EditTripReviewCommand).GetProperties().Select(p => p.Name);
        edit.Should().BeEquivalentTo(["ReviewableRecord", "BookingId", "OverallRating", "Text", "PublishDisplayName", "Version"]);
        Submit().PublishDisplayName.Should().BeFalse();
    }

    private static TripReviewContextDto Context(bool route = false, bool csp = false, bool poi = false) =>
        new(1, TripReviewContextValues.Completed, new(TripReviewContextValues.TourSubject, 1, null), true,
            null, TripReviewContextValues.None, null, new(route, null), new(csp, null), new(poi, null),
            [new(7, "Verified POI")], [], new("T.", null));

    [Fact]
    public void Eligibility_AbsentOptionalInputsRemainValidWhenCapabilitiesUnavailable() =>
        TripReviewInputEligibility.Validate(Submit(), Context()).IsSuccess.Should().BeTrue();

    [Fact]
    public void Eligibility_RejectsEachUnavailableDimensionWithItsOwnCode()
    {
        TripReviewInputEligibility.Validate(Submit(pacing: RoutePacingFeedback.WellPaced), Context(csp: true)).ErrorCode.Should().Be(TripReviewErrorCodes.RoutePacingUnavailable);
        TripReviewInputEligibility.Validate(Submit(csp: 5), Context(route: true)).ErrorCode.Should().Be(TripReviewErrorCodes.CspIneligible);
        TripReviewInputEligibility.Validate(Submit(pois: [new(7, 5)]), Context(route: true, csp: true)).ErrorCode.Should().Be(TripReviewErrorCodes.PoiUnavailable);
    }

    [Fact]
    public void Eligibility_UsesOnlyServerContextPoiIds()
    {
        TripReviewInputEligibility.Validate(Submit(pois: [new(7, 5)], pacing: RoutePacingFeedback.TooLoose, csp: 1), Context(true, true, true)).IsSuccess.Should().BeTrue();
        TripReviewInputEligibility.Validate(Submit(pois: [new(8, 5)]), Context(poi: true)).ErrorCode.Should().Be(TripReviewErrorCodes.PoiIneligible);
    }

    [Fact]
    public void Eligibility_DoesNotAuthorizeAWrongBookingOrReplaceStructuralValidation()
    {
        TripReviewInputEligibility.Validate(Submit(), Context() with { ParentId = 2 }).ErrorCode.Should().Be(TripReviewErrorCodes.BookingNotFound);
        TripReviewInputEligibility.Validate(Submit(title: " "), Context(true, true, true)).ErrorCode.Should().Be(TripReviewErrorCodes.InvalidInput);
    }

    [Fact]
    public void InvalidIdsAndNullPhotoEntries_AreValidationFailuresNotExceptions()
    {
        var validator = new SubmitTripReviewCommandValidator();
        validator.Validate(new SubmitTripReviewCommand(0, 5, "Title", "Content", [], [])).IsValid.Should().BeFalse();
        validator.Validate(new SubmitTripReviewCommand(1, 5, "Title", "Content", [], null)).IsValid.Should().BeFalse();
        validator.Validate(Submit(photos: [null!])).IsValid.Should().BeFalse();
        new EditTripReviewCommandValidator().Validate(new EditTripReviewCommand(-1, 5, "Title", "Content", false, "AQIDBAUGBwg=")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void StructuralFailures_UseTheStableInvalidInputCode()
    {
        var create = new SubmitTripReviewCommand(0, 0, " ", new string('x', 1001), [new(0, 0)], [null!], (RoutePacingFeedback)0, 0);
        var edit = new EditTripReviewCommand(0, 0, " ", new string('x', 1001), false, "invalid");
        var errors = new SubmitTripReviewCommandValidator().Validate(create).Errors
            .Concat(new EditTripReviewCommandValidator().Validate(edit).Errors).ToArray();
        errors.Should().NotBeEmpty();
        errors.Should().OnlyContain(error => error.ErrorCode == TripReviewErrorCodes.InvalidInput);
    }

    [Theory]
    [InlineData("Nguyễn Tiến Đạt", false, "N. T. Đ.")]
    [InlineData("e\u0301 👩‍💻", false, "e\u0301. 👩‍💻.")]
    [InlineData("Nguyễn Tiến Đạt", true, "Nguyễn Tiến Đạt")]
    [InlineData(" ", true, "Traveler")]
    public void PublicName_RequiresConsentForFullName(string name, bool consent, string expected) =>
        TripReview.BuildPublicDisplayName(name, consent).Should().Be(expected);
}
using FluentAssertions;

using TripMate.Application.Features.Admin.Tours.Review;

namespace TripMate.Application.UnitTests.Features.Admin.Tours.Review;

public sealed class ApproveTourCommandValidatorTests
{
    private readonly ApproveTourCommandValidator _validator = new();

    private static TourReviewChecklistDto ValidChecklist() => new(
        ContentCompleteness: true,
        ImageAppropriateness: true,
        PricePlausibility: true,
        PolicyCompliance: true,
        ItineraryFeasibility: true);

    [Fact]
    public void Validate_WhenAllChecklistCriteriaPassed_IsValid()
    {
        var command = new ApproveTourCommand(100, ValidChecklist(), "Looks good.");
        var result = _validator.Validate(command);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenTourIdZeroOrNegative_HasValidationErrorWithCodeMsg01()
    {
        var command = new ApproveTourCommand(0, ValidChecklist());
        var result = _validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ApproveTourCommand.TourId) && e.ErrorCode == "MSG01");
    }

    [Fact]
    public void Validate_WhenChecklistIsNull_HasValidationErrorWithCodeMsg01()
    {
        var command = new ApproveTourCommand(100, null);
        var result = _validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ApproveTourCommand.Checklist) && e.ErrorCode == "MSG01");
    }

    [Theory]
    [InlineData(false, true, true, true, true, "Checklist.ContentCompleteness")]
    [InlineData(true, false, true, true, true, "Checklist.ImageAppropriateness")]
    [InlineData(true, true, false, true, true, "Checklist.PricePlausibility")]
    [InlineData(true, true, true, false, true, "Checklist.PolicyCompliance")]
    [InlineData(true, true, true, true, false, "Checklist.ItineraryFeasibility")]
    public void Validate_WhenAnyChecklistCriterionFalse_HasValidationErrorWithCodeMsg01(
        bool content, bool image, bool price, bool policy, bool itinerary, string failedProperty)
    {
        var checklist = new TourReviewChecklistDto(content, image, price, policy, itinerary);
        var command = new ApproveTourCommand(100, checklist);
        var result = _validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == failedProperty && e.ErrorCode == "MSG01");
    }

    [Fact]
    public void Validate_WhenNoteExceeds1000Characters_HasValidationError()
    {
        var command = new ApproveTourCommand(100, ValidChecklist(), new string('A', 1001));
        var result = _validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ApproveTourCommand.Note));
    }
}
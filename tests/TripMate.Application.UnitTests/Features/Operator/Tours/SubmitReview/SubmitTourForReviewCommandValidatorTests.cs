using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.SubmitReview;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.SubmitReview;

public sealed class SubmitTourForReviewCommandValidatorTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void Validate_OptionalNote(string? note, bool valid) =>
        new SubmitTourForReviewCommandValidator().Validate(new SubmitTourForReviewCommand(1, note)).IsValid.Should().Be(valid);

    [Fact]
    public void Validate_UsesTrimmedUtf16Length()
    {
        var validator = new SubmitTourForReviewCommandValidator();
        validator.Validate(new SubmitTourForReviewCommand(1, "  " + new string('x', Tour.TermsMaxLength) + "  ")).IsValid.Should().BeTrue();
        validator.Validate(new SubmitTourForReviewCommand(1, new string('x', Tour.TermsMaxLength + 1))).IsValid.Should().BeFalse();
        validator.Validate(new SubmitTourForReviewCommand(1, string.Concat(Enumerable.Repeat("\U0001F600", 2001)))).IsValid.Should().BeFalse();
    }
}
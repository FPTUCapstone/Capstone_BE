using FluentValidation;

using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Application.Features.TripReviews.Edit;

public sealed class EditTripReviewCommandValidator : AbstractValidator<EditTripReviewCommand>
{
    public EditTripReviewCommandValidator()
    {
        RuleFor(command => command.BookingId).GreaterThan(0).WithErrorCode(TripReviewErrorCodes.InvalidInput);
        RuleFor(command => command.OverallRating).Must(TripReviewInputRules.IsRating).WithErrorCode(TripReviewErrorCodes.InvalidInput);
        RuleFor(command => command.Text).SetValidator(new ReviewTextValidator());
        RuleFor(command => command.Version).Must(TripReviewInputRules.IsCanonicalVersion).WithErrorCode(TripReviewErrorCodes.InvalidInput);
    }
}
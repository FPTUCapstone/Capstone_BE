using FluentValidation;

using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Tours.SubmitReview;

public sealed class SubmitTourForReviewCommandValidator : AbstractValidator<SubmitTourForReviewCommand>
{
    public SubmitTourForReviewCommandValidator()
    {
        RuleFor(command => command.SubmissionNote).Must(OperatorTourValidation.IsWithinTermsLimit)
            .WithMessage($"At most {Tour.TermsMaxLength} UTF-16 code units are allowed.");
    }
}
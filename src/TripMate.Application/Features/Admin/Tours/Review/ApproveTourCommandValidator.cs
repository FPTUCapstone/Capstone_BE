using FluentValidation;

namespace TripMate.Application.Features.Admin.Tours.Review;

public sealed class ApproveTourCommandValidator : AbstractValidator<ApproveTourCommand>
{
    public const string RequiredFieldErrorCode = "MSG01";
    public const string RequiredFieldMessage = "This field is required.";

    public ApproveTourCommandValidator()
    {
        RuleFor(command => command.TourId)
            .GreaterThan(0)
            .WithErrorCode(RequiredFieldErrorCode)
            .WithMessage(RequiredFieldMessage);

        RuleFor(command => command.Checklist)
            .NotNull()
            .WithErrorCode(RequiredFieldErrorCode)
            .WithMessage(RequiredFieldMessage);

        When(command => command.Checklist is not null, () =>
        {
            RuleFor(command => command.Checklist!.ContentCompleteness)
                .Equal(true)
                .WithErrorCode(RequiredFieldErrorCode)
                .WithMessage(RequiredFieldMessage);

            RuleFor(command => command.Checklist!.ImageAppropriateness)
                .Equal(true)
                .WithErrorCode(RequiredFieldErrorCode)
                .WithMessage(RequiredFieldMessage);

            RuleFor(command => command.Checklist!.PricePlausibility)
                .Equal(true)
                .WithErrorCode(RequiredFieldErrorCode)
                .WithMessage(RequiredFieldMessage);

            RuleFor(command => command.Checklist!.PolicyCompliance)
                .Equal(true)
                .WithErrorCode(RequiredFieldErrorCode)
                .WithMessage(RequiredFieldMessage);

            RuleFor(command => command.Checklist!.ItineraryFeasibility)
                .Equal(true)
                .WithErrorCode(RequiredFieldErrorCode)
                .WithMessage(RequiredFieldMessage);
        });

        RuleFor(command => command.Note)
            .MaximumLength(1000)
            .WithMessage("Review note cannot exceed 1000 characters.");
    }
}
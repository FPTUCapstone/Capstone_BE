using FluentValidation;

namespace TripMate.Application.Features.PointsOfInterest.Deactivate;

public sealed class DeactivatePoiCommandValidator : AbstractValidator<DeactivatePoiCommand>
{
    public const int ReasonMaxLength = 500;

    public DeactivatePoiCommandValidator()
    {
        RuleFor(command => command.PoiId)
            .GreaterThan(0)
            .WithErrorCode(PoiDeactivationErrors.InvalidInput);

        RuleFor(command => command.Reason)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithErrorCode(PoiDeactivationErrors.Msg123)
            .WithMessage(PoiDeactivationErrors.Msg123Message)
            .Must(r => !string.IsNullOrWhiteSpace(r))
            .WithErrorCode(PoiDeactivationErrors.Msg123)
            .WithMessage(PoiDeactivationErrors.Msg123Message)
            .MaximumLength(ReasonMaxLength)
            .WithErrorCode(PoiDeactivationErrors.InvalidInput)
            .WithMessage($"Reason cannot exceed {ReasonMaxLength} characters.");

        RuleFor(command => command.ReferenceSnapshotToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithErrorCode(PoiDeactivationErrors.InvalidInput)
            .Must(t => !string.IsNullOrWhiteSpace(t))
            .WithErrorCode(PoiDeactivationErrors.InvalidInput);
    }
}
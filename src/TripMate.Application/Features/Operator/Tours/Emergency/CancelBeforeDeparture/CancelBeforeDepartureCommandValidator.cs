using FluentValidation;

using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Emergency.CancelBeforeDeparture;

public sealed class CancelBeforeDepartureCommandValidator
    : AbstractValidator<CancelBeforeDepartureCommand>
{
    public CancelBeforeDepartureCommandValidator()
    {
        RuleFor(command => command.TourId).GreaterThan(0);
        RuleFor(command => command.ScheduleId).GreaterThan(0);
        RuleFor(command => command.CommandId).NotEqual(Guid.Empty);
        RuleFor(command => command.ImpactVersion)
            .NotEmpty()
            .Length(EmergencyTourCancellationOperation.HashMaxLength)
            .Matches("^[0-9a-fA-F]+$");
        RuleFor(command => command.Reason).IsInEnum();
        RuleFor(command => command.ReasonDetails)
            .NotEmpty()
            .MaximumLength(EmergencyTourCancellationOperation.ReasonDetailsMaxLength);
        RuleFor(command => command.EmergencyContact)
            .MaximumLength(EmergencyTourCancellationOperation.ContactMaxLength);
        RuleFor(command => command.EvidenceReference)
            .MaximumLength(EmergencyTourCancellationOperation.EvidenceMaxLength);
        RuleFor(command => command.EvidenceUrl)
            .Must(EmergencyEvidenceValidation.IsValidHttpsUrl)
            .WithMessage("Evidence URL must be an absolute HTTPS URL without credentials.");
        RuleFor(command => command.AuthorityReference)
            .MaximumLength(EmergencyTourCancellationOperation.AuthorityReferenceMaxLength);
        RuleFor(command => command)
            .Must(HasAuthorityEvidenceOrDeferral)
            .WithMessage("Authority directives require a reference or evidence-later flag.");
    }

    private static bool HasAuthorityEvidenceOrDeferral(CancelBeforeDepartureCommand command) =>
        command.Reason != EmergencyTourReason.AuthorityDirective
        || !string.IsNullOrWhiteSpace(command.AuthorityReference)
        || !string.IsNullOrWhiteSpace(command.EvidenceReference)
        || command.AddEvidenceLater;
}

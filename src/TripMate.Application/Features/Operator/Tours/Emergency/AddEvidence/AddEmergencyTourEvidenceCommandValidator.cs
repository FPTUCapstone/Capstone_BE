using FluentValidation;

using TripMate.Application.Features.Operator.Tours.Emergency.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Tours.Emergency.AddEvidence;

public sealed class AddEmergencyTourEvidenceCommandValidator
    : AbstractValidator<AddEmergencyTourEvidenceCommand>
{
    public AddEmergencyTourEvidenceCommandValidator()
    {
        RuleFor(command => command.TourId).GreaterThan(0);
        RuleFor(command => command.ScheduleId).GreaterThan(0);
        RuleFor(command => command)
            .Must(command => command.WithdrawEvidence
                || !string.IsNullOrWhiteSpace(command.EvidenceUrl)
                || !string.IsNullOrWhiteSpace(command.AuthorityReference)
                || !string.IsNullOrWhiteSpace(command.EvidenceReference))
            .WithMessage("At least one evidence value is required.");
        RuleFor(command => command)
            .Must(command => !command.WithdrawEvidence
                || (string.IsNullOrWhiteSpace(command.EvidenceUrl)
                    && string.IsNullOrWhiteSpace(command.AuthorityReference)
                    && string.IsNullOrWhiteSpace(command.EvidenceReference)))
            .WithMessage("A withdrawal request must not include evidence values.");
        RuleFor(command => command.EvidenceUrl)
            .Must(EmergencyEvidenceValidation.IsValidHttpsUrl)
            .WithMessage("Evidence URL must be an absolute HTTPS URL without credentials.");
        RuleFor(command => command.AuthorityReference)
            .MaximumLength(EmergencyTourCancellationOperation.AuthorityReferenceMaxLength);
        RuleFor(command => command.EvidenceReference)
            .MaximumLength(EmergencyTourCancellationOperation.EvidenceMaxLength);
    }
}

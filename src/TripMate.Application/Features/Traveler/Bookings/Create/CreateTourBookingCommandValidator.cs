using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Traveler.Bookings.Create;

public sealed class CreateTourBookingCommandValidator : AbstractValidator<CreateTourBookingCommand>
{
    public CreateTourBookingCommandValidator()
    {
        RuleFor(command => command.TourScheduleId).NotEmpty();
        RuleFor(command => command.IdempotencyKey).NotEqual(Guid.Empty);
        RuleFor(command => command.Participants).NotNull().NotEmpty();
        RuleFor(command => command.VoucherCode).MaximumLength(30);

        RuleForEach(command => command.Participants).ChildRules(participant =>
        {
            participant.RuleFor(p => p.FullName).NotEmpty().MaximumLength(BookingParticipant.FullNameMaxLength);
            participant.RuleFor(p => p.ParticipantType).IsInEnum();
            participant.RuleFor(p => p.PhoneNumber).MaximumLength(BookingParticipant.PhoneNumberMaxLength);
            participant.RuleFor(p => p.Email).MaximumLength(BookingParticipant.EmailMaxLength)
                .EmailAddress().When(p => !string.IsNullOrWhiteSpace(p.Email));
            participant.RuleFor(p => p.IdDocumentNumber).MaximumLength(BookingParticipant.IdDocumentNumberMaxLength);
            participant.RuleFor(p => p.Nationality).Length(BookingParticipant.NationalityLength)
                .When(p => !string.IsNullOrWhiteSpace(p.Nationality));
        });
    }
}
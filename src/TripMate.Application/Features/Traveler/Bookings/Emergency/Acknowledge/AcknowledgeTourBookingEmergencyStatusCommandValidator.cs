using FluentValidation;

namespace TripMate.Application.Features.Traveler.Bookings.Emergency.Acknowledge;

public sealed class AcknowledgeTourBookingEmergencyStatusCommandValidator
    : AbstractValidator<AcknowledgeTourBookingEmergencyStatusCommand>
{
    public AcknowledgeTourBookingEmergencyStatusCommandValidator() =>
        RuleFor(command => command.BookingId).GreaterThan(0);
}

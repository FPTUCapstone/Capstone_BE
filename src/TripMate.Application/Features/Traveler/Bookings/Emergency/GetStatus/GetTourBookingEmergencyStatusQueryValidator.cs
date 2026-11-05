using FluentValidation;

namespace TripMate.Application.Features.Traveler.Bookings.Emergency.GetStatus;

public sealed class GetTourBookingEmergencyStatusQueryValidator
    : AbstractValidator<GetTourBookingEmergencyStatusQuery>
{
    public GetTourBookingEmergencyStatusQueryValidator() =>
        RuleFor(query => query.BookingId).GreaterThan(0).WithMessage("Mã đơn đặt tour không hợp lệ.");
}

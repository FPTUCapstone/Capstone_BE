using FluentValidation;

namespace TripMate.Application.Features.Operator.CheckIn.Scan;

public sealed class CheckInTravelerCommandValidator : AbstractValidator<CheckInTravelerCommand>
{
    public CheckInTravelerCommandValidator()
    {
        RuleFor(x => x.ScheduleId)
            .GreaterThan(0)
            .WithMessage("A valid departure schedule id is required.");

        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.QrContent) || !string.IsNullOrWhiteSpace(x.BookingCode))
            .WithMessage("Either a QR code payload or a booking code must be provided.");

        RuleFor(x => x.Latitude)
            .InclusiveBetween(-90m, 90m)
            .When(x => x.Latitude.HasValue)
            .WithMessage("Latitude must be between -90 and 90 degrees.");

        RuleFor(x => x.Longitude)
            .InclusiveBetween(-180m, 180m)
            .When(x => x.Longitude.HasValue)
            .WithMessage("Longitude must be between -180 and 180 degrees.");
    }
}

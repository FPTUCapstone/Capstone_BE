using FluentValidation;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Tours.Departures.Add;

public sealed class AddTourDepartureCommandValidator : AbstractValidator<AddTourDepartureCommand>
{
    private const decimal MaxVndPrice = 9_999_999_999.99m;

    public AddTourDepartureCommandValidator(IDateTimeProvider clock)
    {
        RuleFor(command => command.TourId).GreaterThan(0);
        RuleFor(command => command.StartAtUtc)
            .GreaterThan(_ => clock.UtcNow)
            .WithMessage("Departure start time must be in the future.");
        RuleFor(command => command.EndAtUtc)
            .GreaterThan(command => command.StartAtUtc)
            .WithMessage("Departure end time must be after the start time.");
        RuleFor(command => command.TotalCapacity).GreaterThan(0);
        RuleFor(command => command.MinimumCapacity)
            .GreaterThan(0)
            .LessThanOrEqualTo(command => command.TotalCapacity)
            .WithMessage("Minimum capacity must be between 1 and total capacity.");
        RuleFor(command => command.RegistrationDeadlineAtUtc)
            .GreaterThan(_ => clock.UtcNow)
            .When(command => command.RegistrationDeadlineAtUtc.HasValue)
            .WithMessage("Registration deadline must be in the future.");
        RuleFor(command => command.RegistrationDeadlineAtUtc)
            .LessThan(command => command.StartAtUtc)
            .When(command => command.RegistrationDeadlineAtUtc.HasValue)
            .WithMessage("Registration deadline must be before departure.");
        RuleFor(command => command.AdultPrice)
            .GreaterThan(0m)
            .LessThanOrEqualTo(MaxVndPrice)
            .Must(OperatorTourValidation.IsWholeVnd)
            .When(command => command.AdultPrice.HasValue)
            .WithMessage("Adult price must be a positive whole VND amount.");
        RuleFor(command => command.ChildPrice)
            .GreaterThanOrEqualTo(0m)
            .LessThanOrEqualTo(MaxVndPrice)
            .Must(OperatorTourValidation.IsWholeVnd)
            .When(command => command.ChildPrice.HasValue)
            .WithMessage("Child price must be a non-negative whole VND amount.");
        RuleFor(command => command.InfantPrice)
            .GreaterThanOrEqualTo(0m)
            .LessThanOrEqualTo(MaxVndPrice)
            .Must(OperatorTourValidation.IsWholeVnd)
            .When(command => command.InfantPrice.HasValue)
            .WithMessage("Infant price must be a non-negative whole VND amount.");
        RuleFor(command => command.MeetingPoint)
            .Must(value => value is null || value.Trim().Length <= TourSchedule.MeetingPointMaxLength)
            .WithMessage($"Meeting point must not exceed {TourSchedule.MeetingPointMaxLength} characters.");
        RuleFor(command => command)
            .Must(command => OperatorTourValidation.HasValidMeetingCoordinates(
                new CreateTourScheduleRequest(
                    command.StartAtUtc,
                    command.EndAtUtc,
                    command.MeetingPoint,
                    command.TotalCapacity,
                    command.MinimumCapacity,
                    command.RegistrationDeadlineAtUtc,
                    command.AdultPrice,
                    command.ChildPrice,
                    command.InfantPrice,
                    command.MeetingLatitude,
                    command.MeetingLongitude)))
            .WithMessage("Meeting coordinates require a named point and valid latitude and longitude.");
    }
}
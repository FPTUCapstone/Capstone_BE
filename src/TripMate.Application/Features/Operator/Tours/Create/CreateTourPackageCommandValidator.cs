using FluentValidation;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Tours.Create;

public sealed class CreateTourPackageCommandValidator : AbstractValidator<CreateTourPackageCommand>
{
    public CreateTourPackageCommandValidator(IDateTimeProvider clock)
    {
        RuleFor(command => command.Title).NotEmpty().Must(value => value.Trim().Length <= Tour.TitleMaxLength);
        RuleFor(command => command.Description).NotEmpty();
        RuleFor(command => command.CategoryId).GreaterThan(0).When(command => command.CategoryId.HasValue);
        RuleFor(command => command.BasePrice).GreaterThan(0m).LessThanOrEqualTo(9_999_999_999.99m)
            .Must(OperatorTourValidation.IsWholeVnd).WithMessage("The price must be a whole VND amount.");
        RuleFor(command => command.DurationDays).GreaterThan(0);
        RuleFor(command => command.DestinationIds).NotNull().NotEmpty()
            .Must(OperatorTourValidation.AreValidDestinationIds)
            .WithMessage($"Between 1 and {Tour.MaxDestinations} unique destinations are required.");
        RuleFor(command => command.CancellationPolicyTemplateId).GreaterThan(0);
        RuleFor(command => command.Inclusions).Must(OperatorTourValidation.IsWithinTermsLimit)
            .WithMessage($"At most {Tour.TermsMaxLength} characters are allowed.");
        RuleFor(command => command.Exclusions).Must(OperatorTourValidation.IsWithinTermsLimit)
            .WithMessage($"At most {Tour.TermsMaxLength} characters are allowed.");
        RuleFor(command => command.Highlights).Must(OperatorTourValidation.IsWithinTermsLimit)
            .WithMessage($"At most {Tour.TermsMaxLength} characters are allowed.");
        RuleFor(command => command.TravellerRequirements).Must(OperatorTourValidation.IsWithinTermsLimit)
            .WithMessage($"At most {Tour.TermsMaxLength} characters are allowed.");
        RuleFor(command => command.TimeZoneId).Must(Tour.IsKnownTimeZone)
            .When(command => !string.IsNullOrWhiteSpace(command.TimeZoneId))
            .WithMessage("The time zone must be a valid IANA identifier.");
        When(command => command.AllowDeposit, () =>
        {
            RuleFor(command => command.DepositPercent)
                .NotNull()
                .InclusiveBetween(Tour.MinDepositPercent, Tour.MaxDepositPercent)
                .WithMessage($"Deposit percentage must be between {Tour.MinDepositPercent}% and {Tour.MaxDepositPercent}% when deposits are enabled.");
            RuleFor(command => command.BalanceDueDaysBeforeDeparture)
                .NotNull()
                .InclusiveBetween(Tour.MinBalanceDueDaysBeforeDeparture, Tour.MaxBalanceDueDaysBeforeDeparture)
                .WithMessage($"Balance settlement deadline must be between {Tour.MinBalanceDueDaysBeforeDeparture} and {Tour.MaxBalanceDueDaysBeforeDeparture} days before departure.");
        });
        RuleFor(command => command.ItineraryItems).NotNull().NotEmpty();
        RuleFor(command => command.Schedules).NotNull().NotEmpty();
        RuleForEach(command => command.ItineraryItems).ChildRules(item =>
        {
            item.RuleFor(value => value.DayNo).GreaterThan(0);
            item.RuleFor(value => value.SequenceNo).GreaterThan(0);
            item.RuleFor(value => value.PoiId).GreaterThan(0);
            item.RuleFor(value => value.StayDurationMinutes).GreaterThan(0);
            item.RuleFor(value => value.Notes).Must(value => value is null || value.Trim().Length <= TourItineraryItem.NotesMaxLength)
                .WithMessage($"Activity notes must not exceed {TourItineraryItem.NotesMaxLength} characters.");
        });
        RuleFor(command => command.ItineraryItems).Must(items => items is not null &&
            items.Select(item => (item.DayNo, item.SequenceNo)).Distinct().Count() == items.Count)
            .WithMessage("Each itinerary day and sequence pair must be unique.");
        RuleFor(command => command.ItineraryItems).Must((command, items) =>
            items is not null && items.All(item => item.DayNo <= command.DurationDays))
            .WithMessage("Each itinerary day must be within the tour duration.");
        RuleForEach(command => command.Schedules).ChildRules(schedule =>
        {
            schedule.RuleFor(value => value.StartAtUtc).GreaterThan(_ => clock.UtcNow);
            schedule.RuleFor(value => value.EndAtUtc).GreaterThan(value => value.StartAtUtc);
            schedule.RuleFor(value => value.TotalCapacity).GreaterThan(0);
            schedule.RuleFor(value => value.MinimumCapacity).GreaterThan(0)
                .LessThanOrEqualTo(value => value.TotalCapacity);
            schedule.RuleFor(value => value.RegistrationDeadlineAtUtc)
                .GreaterThan(_ => clock.UtcNow)
                .LessThan(value => value.StartAtUtc)
                .When(value => value.RegistrationDeadlineAtUtc.HasValue);
            schedule.RuleFor(value => value.AdultPrice).GreaterThan(0)
                .LessThanOrEqualTo(9_999_999_999.99m)
                .When(value => value.AdultPrice.HasValue);
            schedule.RuleFor(value => value.ChildPrice).GreaterThanOrEqualTo(0)
                .LessThanOrEqualTo(9_999_999_999.99m)
                .When(value => value.ChildPrice.HasValue);
            schedule.RuleFor(value => value.InfantPrice).GreaterThanOrEqualTo(0)
                .LessThanOrEqualTo(9_999_999_999.99m)
                .When(value => value.InfantPrice.HasValue);
            schedule.RuleFor(value => value.AdultPrice).Must(OperatorTourValidation.IsWholeVnd)
                .WithMessage("The price must be a whole VND amount.");
            schedule.RuleFor(value => value.ChildPrice).Must(OperatorTourValidation.IsWholeVnd)
                .WithMessage("The price must be a whole VND amount.");
            schedule.RuleFor(value => value.InfantPrice).Must(OperatorTourValidation.IsWholeVnd)
                .WithMessage("The price must be a whole VND amount.");
            schedule.RuleFor(value => value.MeetingPoint).Must(value => value is null || value.Trim().Length <= TourSchedule.MeetingPointMaxLength)
                .WithMessage($"Meeting point must not exceed {TourSchedule.MeetingPointMaxLength} characters.");
            schedule.RuleFor(value => value).Must(OperatorTourValidation.HasValidMeetingCoordinates)
                .WithMessage("Meeting coordinates require a named point and valid latitude and longitude.");
        });
        RuleFor(command => command.Schedules)
            .Must(schedules => schedules is null
                || schedules.Select(s => s.StartAtUtc.ToUniversalTime()).Distinct().Count() == schedules.Count)
            .WithMessage("Departure start dates and times must be unique.");
    }
}

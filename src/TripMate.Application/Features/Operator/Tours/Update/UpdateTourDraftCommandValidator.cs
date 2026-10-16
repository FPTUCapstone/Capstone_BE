using FluentValidation;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Tours.Update;

public sealed class UpdateTourDraftCommandValidator : AbstractValidator<UpdateTourDraftCommand>
{
    public UpdateTourDraftCommandValidator(IDateTimeProvider clock)
    {
        RuleFor(command => command.TourId).GreaterThan(0);
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
        RuleFor(command => command.ItineraryItems).NotNull().NotEmpty();
        RuleFor(command => command.Schedules).NotNull().NotEmpty();
        RuleForEach(command => command.ItineraryItems).ChildRules(item =>
        {
            item.RuleFor(value => value.DayNo).GreaterThan(0);
            item.RuleFor(value => value.SequenceNo).GreaterThan(0);
            item.RuleFor(value => value.PoiId).GreaterThan(0);
            item.RuleFor(value => value.StayDurationMinutes).GreaterThan(0);
            item.RuleFor(value => value.Notes).MaximumLength(TourItineraryItem.NotesMaxLength);
        });
        RuleFor(command => command.ItineraryItems).Must(items => items is not null &&
            items.Select(item => (item.DayNo, item.SequenceNo)).Distinct().Count() == items.Count);
        RuleFor(command => command.ItineraryItems).Must((command, items) =>
            items is not null && items.All(item => item.DayNo <= command.DurationDays));
        RuleForEach(command => command.Schedules).ChildRules(schedule =>
        {
            schedule.RuleFor(value => value.StartAtUtc).GreaterThan(clock.UtcNow);
            schedule.RuleFor(value => value.EndAtUtc).GreaterThan(value => value.StartAtUtc);
            schedule.RuleFor(value => value.TotalCapacity).GreaterThan(0);
            schedule.RuleFor(value => value.MinimumCapacity).GreaterThan(0)
                .LessThanOrEqualTo(value => value.TotalCapacity);
            schedule.RuleFor(value => value.RegistrationDeadlineAtUtc)
                .LessThan(value => value.StartAtUtc)
                .When(value => value.RegistrationDeadlineAtUtc.HasValue);
            schedule.RuleFor(value => value.AdultPrice).GreaterThan(0)
                .When(value => value.AdultPrice.HasValue);
            schedule.RuleFor(value => value.ChildPrice).GreaterThanOrEqualTo(0)
                .When(value => value.ChildPrice.HasValue);
            schedule.RuleFor(value => value.InfantPrice).GreaterThanOrEqualTo(0)
                .When(value => value.InfantPrice.HasValue);
            schedule.RuleFor(value => value.AdultPrice).Must(OperatorTourValidation.IsWholeVnd)
                .WithMessage("The price must be a whole VND amount.");
            schedule.RuleFor(value => value.ChildPrice).Must(OperatorTourValidation.IsWholeVnd)
                .WithMessage("The price must be a whole VND amount.");
            schedule.RuleFor(value => value.InfantPrice).Must(OperatorTourValidation.IsWholeVnd)
                .WithMessage("The price must be a whole VND amount.");
            schedule.RuleFor(value => value.MeetingPoint).MaximumLength(TourSchedule.MeetingPointMaxLength);
        });
    }
}
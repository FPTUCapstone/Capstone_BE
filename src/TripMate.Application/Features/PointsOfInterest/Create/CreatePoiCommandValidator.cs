using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.PointsOfInterest.Create;

public sealed class CreatePoiCommandValidator : AbstractValidator<CreatePoiCommand>
{
    public CreatePoiCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .MaximumLength(PointOfInterest.NameMaxLength);

        RuleFor(command => command.CategoryId).GreaterThan(0);
        RuleFor(command => command.Latitude)
            .NotNull()
            .InclusiveBetween(-90m, 90m);
        RuleFor(command => command.Longitude)
            .NotNull()
            .InclusiveBetween(-180m, 180m);

        RuleFor(command => command.Address)
            .MaximumLength(PointOfInterest.AddressMaxLength);
        RuleFor(command => command.Description)
            .MaximumLength(PointOfInterest.DescriptionMaxLength);

        RuleFor(command => command.IndoorOutdoor)
            .Must(value => value is null || Enum.IsDefined(value.Value))
            .WithMessage("Indoor/outdoor must be Indoor, Outdoor, or Mixed.");

        RuleFor(command => command.AverageVisitDurationMinutes)
            .GreaterThan(0)
            .When(command => command.AverageVisitDurationMinutes.HasValue);

        RuleForEach(command => command.TagIds).GreaterThan(0);
        RuleFor(command => command.TagIds)
            .Must(tagIds => tagIds is null || tagIds.Distinct().Count() == tagIds.Count)
            .WithMessage("Tag IDs must be distinct.");

        RuleForEach(command => command.OpeningHours)
            .NotNull()
            .SetValidator(new CreatePoiOpeningHourInputValidator());
        RuleFor(command => command.OpeningHours)
            .Must(hours =>
                hours is null
                || (hours.All(item => item is not null)
                    && hours.Select(item => item.DayOfWeek).Distinct().Count() == hours.Count))
            .WithMessage("Only one opening-hours entry is allowed per day.");
    }
}

internal sealed class CreatePoiOpeningHourInputValidator
    : AbstractValidator<CreatePoiOpeningHourInput>
{
    public CreatePoiOpeningHourInputValidator()
    {
        RuleFor(hours => hours.DayOfWeek)
            .NotNull()
            .InclusiveBetween(0, 6);

        When(hours => hours.IsClosed, () =>
        {
            RuleFor(hours => hours.OpenTime).Null();
            RuleFor(hours => hours.CloseTime).Null();
        });

        When(hours => !hours.IsClosed, () =>
        {
            RuleFor(hours => hours)
                .Must(hours =>
                    hours.OpenTime.HasValue
                    && hours.CloseTime.HasValue
                    && hours.OpenTime.Value < hours.CloseTime.Value)
                .WithMessage("An open day requires an opening time earlier than its closing time.");
        });
    }
}
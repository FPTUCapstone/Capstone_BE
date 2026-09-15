using FluentValidation;

namespace TripMate.Application.Features.Scheduling.Create;

public sealed class CreateSchedulingRequestCommandValidator
    : AbstractValidator<CreateSchedulingRequestCommand>
{
    public CreateSchedulingRequestCommandValidator()
    {
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEqual(Guid.Empty);
        RuleFor(command => command.TimeZoneId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(100)
            .Must(BeKnownTimeZone)
            .WithMessage("Time zone ID is not supported.");
        RuleFor(command => command.StartLatitude).InclusiveBetween(-90m, 90m);
        RuleFor(command => command.StartLongitude).InclusiveBetween(-180m, 180m);
        RuleFor(command => command.ExplorationLatitude).InclusiveBetween(-90m, 90m);
        RuleFor(command => command.ExplorationLongitude).InclusiveBetween(-180m, 180m);
        RuleFor(command => command.AvailableMinutes).InclusiveBetween(60, 720);
        RuleFor(command => command.SearchRadiusKm).InclusiveBetween(1m, 50m);
        RuleFor(command => command.BudgetVnd)
            .GreaterThan(0m)
            .When(command => command.BudgetVnd.HasValue);
        RuleFor(command => command.MandatoryPoiIds)
            .Must(ids => ids is not null)
            .WithMessage("Mandatory locations are required.")
            .Must(ids => ids is null || ids.Count <= 6)
            .WithMessage("You can select up to six mandatory locations.")
            .Must(ids => ids is null || ids.All(id => id > 0))
            .WithMessage("Mandatory location IDs must be positive.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Mandatory locations must be distinct.");
        RuleFor(command => command.EndPoiId)
            .Must((command, endPoiId) => endPoiId.HasValue != command.ReturnToStart)
            .WithMessage("Choose an ending location or return to the start.");
        RuleFor(command => command.EndPoiId)
            .GreaterThan(0)
            .When(command => command.EndPoiId.HasValue);
        RuleFor(command => command.TransportMode).IsInEnum();
        RuleFor(command => command.RestPreference).IsInEnum();
        RuleFor(command => command)
            .Must(EndOnSameLocalDay)
            .WithMessage("The itinerary must end on the same local calendar day.");
    }

    private static bool BeKnownTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static bool EndOnSameLocalDay(CreateSchedulingRequestCommand command)
    {
        if (!BeKnownTimeZone(command.TimeZoneId))
        {
            return true;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(command.TimeZoneId.Trim());
        var localStart = TimeZoneInfo.ConvertTime(command.StartAt, timeZone);
        var localEnd = TimeZoneInfo.ConvertTime(command.StartAt.AddMinutes(command.AvailableMinutes), timeZone);
        return localStart.Date == localEnd.Date;
    }
}

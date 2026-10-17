using FluentValidation;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Scheduling.Create;

public sealed class CreateSchedulingRequestCommandValidator
    : AbstractValidator<CreateSchedulingRequestCommand>
{
    private readonly IDateTimeProvider _dateTimeProvider;

    public CreateSchedulingRequestCommandValidator(IDateTimeProvider dateTimeProvider)
    {
        _dateTimeProvider = dateTimeProvider;
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEqual(Guid.Empty);
        RuleFor(command => command.TimeZoneId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(100)
            .Must(BeKnownTimeZone)
            .WithMessage("Time zone ID is not supported.");
        RuleFor(command => command.StartAt)
            .Must((command, startAt) => NotInPast(command.TimeZoneId, startAt))
            .WithMessage("Start time must not be in the past.");
        RuleFor(command => command.StartLatitude).InclusiveBetween(-90m, 90m);
        RuleFor(command => command.StartLongitude).InclusiveBetween(-180m, 180m);
        RuleFor(command => command.ExplorationLatitude).InclusiveBetween(-90m, 90m);
        RuleFor(command => command.ExplorationLongitude).InclusiveBetween(-180m, 180m);
        RuleFor(command => command.AvailableMinutes)
            .InclusiveBetween(SchedulingRequest.MinAvailableMinutes, SchedulingRequest.MaxAvailableMinutes);
        RuleFor(command => command.SearchRadiusKm)
            .InclusiveBetween(SchedulingRequest.MinSearchRadiusKm, SchedulingRequest.MaxSearchRadiusKm);
        RuleFor(command => command.BudgetVnd)
            .GreaterThan(0m)
            .When(command => command.BudgetVnd.HasValue);
        RuleFor(command => command.MandatoryPoiIds)
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
    }

    private bool NotInPast(string? timeZoneId, DateTimeOffset startAt)
    {
        if (!BeKnownTimeZone(timeZoneId))
        {
            return true;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId!.Trim());
        var localStart = TimeZoneInfo.ConvertTime(startAt, timeZone);
        var localNow = TimeZoneInfo.ConvertTime(_dateTimeProvider.UtcNow, timeZone);
        return localStart >= localNow;
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
}
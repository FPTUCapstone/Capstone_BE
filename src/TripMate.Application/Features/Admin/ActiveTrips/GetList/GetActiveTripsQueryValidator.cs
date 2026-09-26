using FluentValidation;

namespace TripMate.Application.Features.Admin.ActiveTrips.GetList;

public sealed class GetActiveTripsQueryValidator : AbstractValidator<GetActiveTripsQuery>
{
    public GetActiveTripsQueryValidator()
    {
        RuleFor(query => query.Keyword)
            .Must(value => value is null || value.Trim().Length <= GetActiveTripsQuery.MaximumKeywordLength)
            .WithMessage($"Keyword must not exceed {GetActiveTripsQuery.MaximumKeywordLength} characters.");
        RuleFor(query => query.Destination)
            .Must(value => value is null || value.Trim().Length <= GetActiveTripsQuery.MaximumDestinationLength)
            .WithMessage($"Destination must not exceed {GetActiveTripsQuery.MaximumDestinationLength} characters.");
        RuleFor(query => query.TripType)
            .Must(value => string.IsNullOrWhiteSpace(value) || GetActiveTripsQuery.AllowedTripTypes.Contains(value))
            .WithMessage("TripType is invalid.");
        RuleFor(query => query.AlertState)
            .Must(value => string.IsNullOrWhiteSpace(value) || GetActiveTripsQuery.AllowedAlertStates.Contains(value))
            .WithMessage("AlertState is invalid.");
        RuleFor(query => query.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, GetActiveTripsQuery.MaximumPageSize);
        RuleFor(query => query.StartDateTo)
            .Must(value => !value.HasValue || value.Value < DateOnly.MaxValue)
            .WithMessage("StartDateTo is outside the supported range.");
        RuleFor(query => query)
            .Must(query => !query.StartDateFrom.HasValue || !query.StartDateTo.HasValue || query.StartDateFrom <= query.StartDateTo)
            .WithMessage("The submitted Start Date range is logically invalid.");
    }
}
using FluentValidation;

using TripMate.Application.Features.TripHistory.Common;

namespace TripMate.Application.Features.TripHistory.List;

/// <summary>
/// Rule order is the contract: the error middleware reports the first failure's code, so a request
/// with several problems always gets the same code (tab, trip type, dates, paging).
/// </summary>
public sealed class ListTravelerTripsQueryValidator : AbstractValidator<ListTravelerTripsQuery>
{
    public ListTravelerTripsQueryValidator()
    {
        RuleFor(query => query.TravelerUserId)
            .GreaterThan(0)
            .WithErrorCode(TripHistoryErrorCodes.Unauthorized);

        RuleFor(query => query.Tab)
            .Must(tab => tab is not null && ListTravelerTripsQuery.AllowedTabs.Contains(tab, StringComparer.Ordinal))
            .WithErrorCode(TripHistoryErrorCodes.InvalidTab)
            .WithMessage("The tab must be Upcoming, Completed or Cancelled.");

        RuleFor(query => query.TripType)
            .Must(type => type is null
                || ListTravelerTripsQuery.AllowedTripTypes.Contains(type, StringComparer.Ordinal))
            .WithErrorCode(TripHistoryErrorCodes.InvalidTripType)
            .WithMessage("The trip type must be All, TourBooking or SelfPlannedItinerary.");

        RuleFor(query => query)
            .Must(query => VietnamDayRange.TryCreate(query.FromDate, query.ToDate, out _))
            .OverridePropertyName(nameof(ListTravelerTripsQuery.FromDate))
            .WithErrorCode(TripHistoryErrorCodes.InvalidDateRange)
            .WithMessage("The dates must be yyyy-MM-dd and the start date must not be after the end date.");

        RuleFor(query => query.Page)
            .GreaterThanOrEqualTo(1)
            .WithErrorCode(TripHistoryErrorCodes.InvalidPaging);

        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, ListTravelerTripsQuery.MaxPageSize)
            .WithErrorCode(TripHistoryErrorCodes.InvalidPaging);

        RuleFor(query => query)
            .Must(query => (long)query.Page * query.PageSize <= ListTravelerTripsQuery.MaxWindowKeys)
            .OverridePropertyName(nameof(ListTravelerTripsQuery.Page))
            .WithErrorCode(TripHistoryErrorCodes.InvalidPaging)
            .WithMessage($"Page times page size must not exceed {ListTravelerTripsQuery.MaxWindowKeys}.");
    }
}
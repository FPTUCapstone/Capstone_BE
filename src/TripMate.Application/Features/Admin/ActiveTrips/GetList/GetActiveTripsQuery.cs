using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.ActiveTrips.GetList;

public sealed record GetActiveTripsQuery(
    string? Keyword = null,
    string? TripType = null,
    string? Destination = null,
    DateOnly? StartDateFrom = null,
    DateOnly? StartDateTo = null,
    string? AlertState = null,
    int PageNumber = 1,
    int PageSize = GetActiveTripsQuery.DefaultPageSize)
    : IRequest<Result<ActiveTripsResponseDto>>
{
    public const string SelfPlannedType = "SelfPlanned";
    public const string TourType = "Tour";
    public const string WithOpenAlerts = "WithOpenAlerts";
    public const string WithoutOpenAlerts = "WithoutOpenAlerts";
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
    public const int MaximumKeywordLength = 200;
    public const int MaximumDestinationLength = 300;

    public static readonly string[] AllowedTripTypes = [SelfPlannedType, TourType];
    public static readonly string[] AllowedAlertStates = [WithOpenAlerts, WithoutOpenAlerts];
}

public sealed record ActiveTripListItemDto(
    string TripId,
    string TripCode,
    string TripType,
    string CurrentState,
    string GroupOrTraveler,
    string? Destination,
    DateTimeOffset? StartedAtUtc,
    int? CurrentDay,
    int Members,
    int OpenAlerts);

public sealed record ActiveTripSummaryDto(
    int ActiveTrips,
    int TripsWithOpenAlerts,
    int TravelersOnTrip);

public sealed record ActiveTripsResponseDto(
    ActiveTripSummaryDto Summary,
    int PageNumber,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<ActiveTripListItemDto> Items);
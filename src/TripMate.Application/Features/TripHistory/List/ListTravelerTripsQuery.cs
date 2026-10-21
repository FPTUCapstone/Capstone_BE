using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripHistory.Common;

namespace TripMate.Application.Features.TripHistory.List;

/// <summary>
/// UC-32 list request. Dates arrive as <c>yyyy-MM-dd</c> text so a malformed value reaches the
/// validator and produces the stable <c>trip_history.invalid_date_range</c> code.
/// </summary>
public sealed record ListTravelerTripsQuery(
    long TravelerUserId,
    string? Tab,
    string? TripType,
    string? FromDate,
    string? ToDate,
    int Page,
    int PageSize) : IRequest<Result<TripHistoryPage>>
{
    public const string TabUpcoming = "Upcoming";
    public const string TabCompleted = "Completed";
    public const string TabCancelled = "Cancelled";

    public const string TripTypeAll = "All";
    public const string TripTypeTourBooking = "TourBooking";
    public const string TripTypeSelfPlannedItinerary = "SelfPlannedItinerary";

    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    /// <summary>Upper bound for <c>page * pageSize</c>: the most keys read from one stream per request.</summary>
    public const int MaxWindowKeys = 1000;

    public static readonly string[] AllowedTabs = [TabUpcoming, TabCompleted, TabCancelled];

    public static readonly string[] AllowedTripTypes =
        [TripTypeAll, TripTypeTourBooking, TripTypeSelfPlannedItinerary];
}
using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Weather.PublicForecast;

namespace TripMate.Application.Features.Weather.UpcomingTrips;

/// <summary>
/// Upcoming Trip Weather: the signed-in Traveler's confirmed departures that start within the forecast horizon
/// (or are under way), each with the public forecast at its meeting point and any open UC-66 safety review.
/// </summary>
public sealed record GetUpcomingTripWeatherQuery(long TravelerUserId) : IRequest<Result<UpcomingTripWeatherResponse>>;

public sealed record UpcomingTripWeatherResponse(IReadOnlyList<UpcomingTripWeatherDto> Trips);

public sealed record UpcomingTripWeatherDto(
    long BookingId,
    string BookingCode,
    long TourScheduleId,
    string TourTitle,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    bool InProgress,
    UpcomingTripPointDto? Point,
    string ForecastDate,
    PublicWeatherDayDto? Forecast,
    UpcomingTripWarningDto? Warning);

/// <summary>Where the card's forecast is taken: the meeting point, else the first itinerary stop.</summary>
public sealed record UpcomingTripPointDto(string? Name, decimal Latitude, decimal Longitude);

/// <summary>An open UC-66 Weather Safety Review, repeated for the Traveler; the decision stays with the Operator.</summary>
public sealed record UpcomingTripWarningDto(
    string ReviewStatus,
    string? EventType,
    string? Severity,
    string? Description,
    DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ValidToUtc,
    string? OperatorInstructions);
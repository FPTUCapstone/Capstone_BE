using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Operator.Tours.Weather;

public sealed record WeatherStopReview(long TourItemId, bool Required);

public sealed record WeatherDepartureReview(long ScheduleId, string MeetingPoint,
    decimal Latitude, decimal Longitude);

public sealed record ReviewTourWeatherReadinessCommand(long TourId,
    IReadOnlyList<WeatherStopReview> Stops,
    IReadOnlyList<WeatherDepartureReview> Departures)
    : IRequest<Result<ReviewTourWeatherReadinessResponse>>;

public sealed record ReviewTourWeatherReadinessResponse(long TourId,
    int ReviewedStops, int ReviewedDepartures);

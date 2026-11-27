using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Weather.PublicForecast;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Weather.UpcomingTrips;

public sealed class GetUpcomingTripWeatherQueryHandler(
    IApplicationDbContext context,
    IPublicWeatherForecastProvider forecastProvider,
    IDateTimeProvider clock)
    : IRequestHandler<GetUpcomingTripWeatherQuery, Result<UpcomingTripWeatherResponse>>
{
    /// <summary>Tomorrow.io's daily forecast reaches about five days ahead.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(5);

    public const int MaxTrips = 3;

    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task<Result<UpcomingTripWeatherResponse>> Handle(GetUpcomingTripWeatherQuery request,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var horizonEnd = now + Horizon;

        var trips = await context.Bookings
            .AsNoTracking()
            .Where(booking => booking.TravelerUserId == request.TravelerUserId
                && booking.Status == BookingStatus.Confirmed
                && booking.TourSchedule.EndAtUtc > now
                && booking.TourSchedule.StartAtUtc <= horizonEnd)
            .OrderBy(booking => booking.TourSchedule.StartAtUtc)
            .ThenBy(booking => booking.Id)
            .Take(MaxTrips)
            .Select(booking => new
            {
                booking.Id,
                booking.BookingCode,
                booking.TourScheduleId,
                booking.TourSchedule.TourId,
                booking.TourSchedule.Tour.Title,
                booking.TourSchedule.StartAtUtc,
                booking.TourSchedule.EndAtUtc,
                booking.TourSchedule.MeetingPoint,
                booking.TourSchedule.MeetingLatitude,
                booking.TourSchedule.MeetingLongitude,
            })
            .ToListAsync(cancellationToken);

        if (trips.Count == 0)
        {
            return Result<UpcomingTripWeatherResponse>.Success(new UpcomingTripWeatherResponse([]));
        }

        var tourIds = trips.Select(trip => trip.TourId).Distinct().ToList();
        var firstStops = (await context.TourItineraryItems
                .AsNoTracking()
                .Where(item => tourIds.Contains(item.TourId))
                .Select(item => new { item.TourId, item.DayNo, item.SequenceNo, item.Poi.Name, item.Poi.Latitude, item.Poi.Longitude })
                .ToListAsync(cancellationToken))
            .GroupBy(stop => stop.TourId)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(stop => stop.DayNo).ThenBy(stop => stop.SequenceNo)
                .Select(stop => new UpcomingTripPointDto(stop.Name, stop.Latitude, stop.Longitude))
                .First());

        var warnings = await LoadWarningsAsync(trips.Select(trip => trip.TourScheduleId).ToList(), cancellationToken);
        var todayLocal = LocalDate(now);
        var result = new List<UpcomingTripWeatherDto>(trips.Count);

        foreach (var trip in trips)
        {
            var point = trip is { MeetingLatitude: { } latitude, MeetingLongitude: { } longitude }
                ? new UpcomingTripPointDto(trip.MeetingPoint, latitude, longitude)
                : firstStops.GetValueOrDefault(trip.TourId);
            var startLocal = LocalDate(trip.StartAtUtc);
            var forecastDate = string.CompareOrdinal(startLocal, todayLocal) < 0 ? todayLocal : startLocal;
            var forecast = point is null ? null : await ForecastForAsync(point, forecastDate, cancellationToken);

            result.Add(new UpcomingTripWeatherDto(trip.Id, trip.BookingCode, trip.TourScheduleId, trip.Title,
                trip.StartAtUtc, trip.EndAtUtc, InProgress: trip.StartAtUtc <= now, point, forecastDate, forecast,
                warnings.GetValueOrDefault(trip.TourScheduleId)));
        }

        return Result<UpcomingTripWeatherResponse>.Success(new UpcomingTripWeatherResponse(result));
    }

    private async Task<Dictionary<long, UpcomingTripWarningDto>> LoadWarningsAsync(List<long> scheduleIds,
        CancellationToken cancellationToken)
    {
        var reviews = await context.WeatherSafetyReviews
            .AsNoTracking()
            .Where(review => scheduleIds.Contains(review.TourScheduleId)
                && review.Status != WeatherSafetyReview.StatusClosed)
            .ToListAsync(cancellationToken);
        if (reviews.Count == 0)
        {
            return [];
        }

        var eventIds = reviews.Select(review => review.ProviderEventId).Distinct().ToList();
        var events = await context.WeatherEvents
            .AsNoTracking()
            .Where(evt => evt.ProviderEventId != null && eventIds.Contains(evt.ProviderEventId))
            .ToListAsync(cancellationToken);

        // The newest open review of a departure is the one the Operator is working on.
        return reviews
            .GroupBy(review => review.TourScheduleId)
            .ToDictionary(group => group.Key, group =>
            {
                var review = group.OrderByDescending(item => item.UrgentNoticeSentAtUtc).ThenByDescending(item => item.Id).First();
                var evt = events.FirstOrDefault(item =>
                    item.Provider == review.Provider && item.ProviderEventId == review.ProviderEventId);
                return new UpcomingTripWarningDto(review.Status, evt?.EventType, evt?.Severity, evt?.Description,
                    evt?.ValidFromUtc, evt?.ValidToUtc, review.ImmediateInstructions);
            });
    }

    private async Task<PublicWeatherDayDto?> ForecastForAsync(UpcomingTripPointDto point, string localDate,
        CancellationToken cancellationToken)
    {
        if (point.Latitude is < GetPublicWeatherForecastQuery.MinLatitude or > GetPublicWeatherForecastQuery.MaxLatitude
            || point.Longitude is < GetPublicWeatherForecastQuery.MinLongitude or > GetPublicWeatherForecastQuery.MaxLongitude)
        {
            return null;
        }

        // Same 0.01° cell as the public page, so both share one cached Tomorrow.io call.
        var data = await forecastProvider.GetAsync(
            Math.Round(point.Latitude, GetPublicWeatherForecastQuery.CoordinateDecimals, MidpointRounding.AwayFromZero),
            Math.Round(point.Longitude, GetPublicWeatherForecastQuery.CoordinateDecimals, MidpointRounding.AwayFromZero),
            cancellationToken);
        return data?.Daily.FirstOrDefault(day => day.Date == localDate);
    }

    private static string LocalDate(DateTimeOffset instant) =>
        instant.ToOffset(VietnamOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
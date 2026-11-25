using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.WeatherTips;

/// <summary>
/// Builds the day-level "what to bring" list from the forecast hours of the visit stops. Whenever any stop has no
/// forecast the answer is <c>null</c>: the weather is never guessed, and a weather failure never fails the request.
/// </summary>
public sealed class GetItineraryWeatherTipsQueryHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService accessService,
    IDayForecastProvider forecastProvider,
    IDateTimeProvider clock,
    IOptions<WeatherTipsOptions> options,
    ILogger<GetItineraryWeatherTipsQueryHandler> logger)
    : IRequestHandler<GetItineraryWeatherTipsQuery, Result<ItineraryWeatherTipsResponse>>
{
    public const string TimeZoneId = "Asia/Ho_Chi_Minh";
    public const int MaxLocationCells = 3;

    // Vietnam has no daylight saving, so a fixed offset is the same as the zone and needs no tz database.
    private static readonly TimeSpan LocalOffset = TimeSpan.FromHours(7);

    public async Task<Result<ItineraryWeatherTipsResponse>> Handle(
        GetItineraryWeatherTipsQuery request,
        CancellationToken cancellationToken)
    {
        var accessResult = await accessService.ResolveCurrentAsync(
            request.ItineraryId, request.TravelerUserId, trackCurrent: false, cancellationToken);
        if (accessResult.IsFailure)
        {
            return Result.Failure<ItineraryWeatherTipsResponse>(accessResult.ErrorCode!, accessResult.ErrorMessage!);
        }

        var itineraryId = accessResult.Value.CurrentItinerary.Id;
        if (!options.Value.Enabled)
        {
            return Unavailable(itineraryId, "disabled");
        }

        var stops = await LoadStopsAsync(itineraryId, cancellationToken);
        if (stops.Count == 0)
        {
            return Unavailable(itineraryId, "no-coordinates");
        }

        if (stops.All(stop => stop.DepartureUtc <= clock.UtcNow))
        {
            return Unavailable(itineraryId, "past");
        }

        var cells = stops.Select(stop => stop.Cell).Distinct().ToArray();
        if (cells.Length > MaxLocationCells)
        {
            return Unavailable(itineraryId, "too-many-locations");
        }

        IReadOnlyList<HourlyForecast>?[] series;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            series = await Task.WhenAll(cells.Select(cell =>
                forecastProvider.GetHourlyAsync(cell.Latitude, cell.Longitude, timeout.Token)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(itineraryId, "timeout");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Weather tips for itinerary {ItineraryId}: provider-error ({ExceptionType})",
                itineraryId, exception.GetType().Name);
            return Result.Success(new ItineraryWeatherTipsResponse(null));
        }

        if (series.Any(hours => hours is null))
        {
            return Unavailable(itineraryId, "no-forecast");
        }

        var byCell = cells
            .Zip(series, (cell, hours) => (cell, hours: hours!
                .GroupBy(hour => hour.StartUtc)
                .ToDictionary(group => group.Key, group => group.First())))
            .ToDictionary(entry => entry.cell, entry => entry.hours);
        var covering = new Dictionary<(Cell, DateTimeOffset), HourlyForecast>();
        foreach (var stop in stops)
        {
            foreach (var hourStart in WeatherTipRules.HourStartsCovering(stop.ArrivalUtc, stop.DepartureUtc))
            {
                if (!byCell[stop.Cell].TryGetValue(hourStart, out var hour))
                {
                    return Unavailable(itineraryId, "out-of-range");
                }

                covering[(stop.Cell, hourStart)] = hour;
            }
        }

        var (summary, tips) = WeatherTipRules.Evaluate(covering.Values);
        var firstArrival = stops.Min(stop => stop.ArrivalUtc);
        var dto = new WeatherTipsDto(
            firstArrival.ToOffset(LocalOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeZoneId,
            clock.UtcNow,
            summary,
            tips);
        logger.LogInformation(
            "Weather tips for itinerary {ItineraryId}: {Outcome}", itineraryId, tips.Count == 0 ? "no-tips" : "success");
        return Result.Success(new ItineraryWeatherTipsResponse(dto));
    }

    private async Task<List<Stop>> LoadStopsAsync(long itineraryId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.ItineraryItems
            .AsNoTracking()
            .Where(item => item.ItineraryId == itineraryId
                && item.Kind == ItineraryItemKind.Visit
                && item.PlannedArrivalUtc != null
                && item.PlannedDepartureUtc != null
                && item.PointOfInterest != null)
            .Select(item => new
            {
                Arrival = item.PlannedArrivalUtc!.Value,
                Departure = item.PlannedDepartureUtc!.Value,
                item.PointOfInterest!.Latitude,
                item.PointOfInterest.Longitude,
            })
            .ToListAsync(cancellationToken);
        return rows
            .Select(row => new Stop(row.Arrival, row.Departure, Cell.Of(row.Latitude, row.Longitude)))
            .ToList();
    }

    private Result<ItineraryWeatherTipsResponse> Unavailable(long itineraryId, string outcome)
    {
        logger.LogInformation("Weather tips for itinerary {ItineraryId}: {Outcome}", itineraryId, outcome);
        return Result.Success(new ItineraryWeatherTipsResponse(null));
    }

    /// <summary>About 11 km wide. Its centre is what the forecast is asked for, so nearby stops share one call.</summary>
    private readonly record struct Cell(decimal Latitude, decimal Longitude)
    {
        public static Cell Of(decimal latitude, decimal longitude) => new(
            Math.Round(latitude, 1, MidpointRounding.AwayFromZero),
            Math.Round(longitude, 1, MidpointRounding.AwayFromZero));
    }

    private sealed record Stop(DateTimeOffset ArrivalUtc, DateTimeOffset DepartureUtc, Cell Cell);
}
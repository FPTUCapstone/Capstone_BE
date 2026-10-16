using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tours.Shared;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tours.GetDetail;

/// <summary>
/// UC-26. One consistent snapshot, a fixed number of set-based queries (independent of how many
/// departures, stops or images a tour has), and an explicit projection of every public field.
/// </summary>
public sealed class GetTourDetailQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    ILogger<GetTourDetailQueryHandler> logger)
    : IRequestHandler<GetTourDetailQuery, Result<TourDetailDto>>
{
    private const string Currency = "VND";

    public async Task<Result<TourDetailDto>> Handle(
        GetTourDetailQuery request,
        CancellationToken cancellationToken)
    {
        if (request.TourId <= 0) return NotFound();

        var now = dateTimeProvider.UtcNow.ToUniversalTime();
        return await dbContext.ExecuteInSerializableTransactionAsync(
            async transactionCancellationToken =>
            {
                var tour = await dbContext.Tours.AsNoTracking()
                    .Where(TourVisibility.PublicTour(now))
                    .Where(candidate => candidate.Id == request.TourId)
                    .Select(candidate => new
                    {
                        candidate.Id,
                        candidate.Title,
                        candidate.Description,
                        candidate.DestinationSummary,
                        candidate.DurationDays,
                        candidate.BasePrice,
                        candidate.Inclusions,
                        candidate.Exclusions,
                        OperatorName = candidate.OperatorProfile.CompanyName,
                        OperatorPhone = candidate.OperatorProfile.ContactPhone,
                    })
                    .SingleOrDefaultAsync(transactionCancellationToken);
                if (tour is null) return NotFound();

                var destinations = await dbContext.TourDestinations.AsNoTracking()
                    .Where(link => link.TourId == tour.Id)
                    .OrderBy(link => link.SequenceNo)
                    .Select(link => link.Destination.Name)
                    .ToListAsync(transactionCancellationToken);

                var images = await dbContext.TourMedia.AsNoTracking()
                    .Where(media => media.TourId == tour.Id
                        && media.LifecycleStatus == TourMediaLifecycleStatus.Active)
                    .OrderByDescending(media => media.IsPrimary)
                    .ThenBy(media => media.SortOrder)
                    .ThenBy(media => media.Id)
                    .Select(media => new TourDetailImageDto(
                        media.DeliveryUrl, media.AltText, media.Caption, media.IsPrimary))
                    .ToListAsync(transactionCancellationToken);

                var rules = await dbContext.TourCancellationPolicyRules.AsNoTracking()
                    .Where(rule => rule.TourId == tour.Id)
                    .OrderByDescending(rule => rule.DaysBeforeDeparture)
                    .Select(rule => new TourDetailCancellationRuleDto(
                        rule.DaysBeforeDeparture, rule.CancellationFeePercent))
                    .ToListAsync(transactionCancellationToken);

                // A stop stays visible even if its catalogue entry was deactivated after approval:
                // the approved itinerary is the commercial record, so hiding a stop would misstate it.
                var stops = await dbContext.TourItineraryItems.AsNoTracking()
                    .Where(item => item.TourId == tour.Id)
                    .OrderBy(item => item.DayNo)
                    .ThenBy(item => item.SequenceNo)
                    .Select(item => new
                    {
                        item.DayNo,
                        item.SequenceNo,
                        item.PoiId,
                        PoiName = item.Poi.Name,
                        PoiAddress = item.Poi.Address,
                        item.Poi.Latitude,
                        item.Poi.Longitude,
                        item.PlannedTime,
                        item.StayDurationMinutes,
                        item.Notes,
                    })
                    .ToListAsync(transactionCancellationToken);

                var departures = await dbContext.TourSchedules.AsNoTracking()
                    .Where(schedule => schedule.TourId == tour.Id
                        && schedule.Status == TourScheduleStatus.Scheduled
                        && schedule.StartAtUtc > now
                        && schedule.EndAtUtc > schedule.StartAtUtc)
                    .OrderBy(schedule => schedule.StartAtUtc)
                    .ThenBy(schedule => schedule.Id)
                    .Select(schedule => new
                    {
                        schedule.Id,
                        schedule.StartAtUtc,
                        schedule.EndAtUtc,
                        schedule.MeetingPoint,
                        schedule.RegistrationDeadlineAtUtc,
                        schedule.AdultPrice,
                        schedule.ChildPrice,
                        schedule.InfantPrice,
                        schedule.TotalCapacity,
                        schedule.ReservedCapacity,
                    })
                    .ToListAsync(transactionCancellationToken);

                var aggregate = await TripReviewAggregateReader.ReadTourAsync(
                    dbContext, tour.Id, logger, transactionCancellationToken);
                if (aggregate.IsFailure)
                {
                    return Result.Failure<TourDetailDto>(
                        aggregate.ErrorCode ?? "tour.rating_unavailable",
                        aggregate.ErrorMessage ?? "The tour rating could not be read.");
                }

                var schedules = departures
                    .Select(departure =>
                    {
                        var availability = TourAvailability.Classify(
                            departure.TotalCapacity, departure.ReservedCapacity);
                        if (availability == TourAvailability.Unknown)
                        {
                            logger.LogWarning(
                                "UC-26 availability unknown for tour {TourId}: invalid schedule capacity snapshot.",
                                tour.Id);
                        }

                        var registrationOpen = departure.RegistrationDeadlineAtUtc is null
                            || now < departure.RegistrationDeadlineAtUtc.Value;
                        return new TourDetailScheduleDto(
                            departure.Id.ToString(CultureInfo.InvariantCulture),
                            departure.StartAtUtc.UtcDateTime,
                            departure.EndAtUtc.UtcDateTime,
                            departure.MeetingPoint,
                            departure.RegistrationDeadlineAtUtc?.UtcDateTime,
                            ToVnd(departure.AdultPrice ?? tour.BasePrice),
                            departure.ChildPrice.HasValue ? ToVnd(departure.ChildPrice.Value) : null,
                            departure.InfantPrice.HasValue ? ToVnd(departure.InfantPrice.Value) : null,
                            TourAvailability.RemainingSlots(departure.TotalCapacity, departure.ReservedCapacity),
                            availability,
                            availability == TourAvailability.Available && registrationOpen);
                    })
                    .ToList();

                var itinerary = stops
                    .GroupBy(stop => stop.DayNo)
                    .OrderBy(day => day.Key)
                    .Select(day => new TourDetailItineraryDayDto(
                        day.Key,
                        day.OrderBy(stop => stop.SequenceNo)
                            .Select(stop => new TourDetailItineraryItemDto(
                                stop.SequenceNo,
                                new TourDetailPoiDto(
                                    stop.PoiId.ToString(CultureInfo.InvariantCulture),
                                    stop.PoiName,
                                    stop.PoiAddress,
                                    stop.Latitude,
                                    stop.Longitude),
                                stop.PlannedTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
                                stop.StayDurationMinutes,
                                stop.Notes))
                            .ToList()))
                    .ToList();

                return Result.Success(new TourDetailDto(
                    tour.Id.ToString(CultureInfo.InvariantCulture),
                    tour.Title,
                    tour.Description,
                    tour.DestinationSummary,
                    destinations,
                    tour.DurationDays,
                    ToVnd(tour.BasePrice),
                    Currency,
                    new TourDetailOperatorDto(tour.OperatorName, tour.OperatorPhone),
                    images,
                    tour.Inclusions,
                    tour.Exclusions,
                    new TourDetailCancellationPolicyDto(rules),
                    itinerary,
                    schedules,
                    schedules.Any(schedule => schedule.Bookable),
                    new TourDetailRatingDto(aggregate.Value.Average, aggregate.Value.Count),
                    now.UtcDateTime));
            },
            cancellationToken);
    }

    private static long ToVnd(decimal amount) => checked((long)decimal.Truncate(amount));

    private static Result<TourDetailDto> NotFound() =>
        Result.Failure<TourDetailDto>(TourErrorCodes.NotFound, "The tour is unavailable.");
}

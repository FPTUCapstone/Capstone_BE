using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tours.Search;

public sealed class SearchToursQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    ILogger<SearchToursQueryHandler> logger)
    : IRequestHandler<SearchToursQuery, Result<PagedToursResponseDto>>
{
    private const string Currency = "VND";

    public async Task<Result<PagedToursResponseDto>> Handle(
        SearchToursQuery request,
        CancellationToken cancellationToken)
    {
        var destination = TourSearchCriteria.NormalizeDestination(request.Destination);
        var now = dateTimeProvider.UtcNow.ToUniversalTime();
        var hasDepartureDate = request.DepartureDate.HasValue;
        var lowerInclusive = default(DateTimeOffset);
        var upperExclusive = default(DateTimeOffset);

        if (hasDepartureDate
            && !TourSearchCriteria.TryGetDepartureBoundsUtc(
                request.DepartureDate!.Value,
                out lowerInclusive,
                out upperExclusive))
        {
            throw new InvalidOperationException(
                "SearchToursQuery must be validated before it reaches the handler.");
        }

        var offset = checked((request.Page - 1) * request.PageSize);

        return await dbContext.ExecuteInSerializableTransactionAsync(
            async transactionCancellationToken =>
            {
                var tours = dbContext.Tours
                    .AsNoTracking()
                    .Where(tour =>
                        tour.Status == TourStatus.Approved
                        && tour.PublishedAtUtc.HasValue
                        && tour.PublishedAtUtc.Value <= now
                        && tour.OperatorProfile.ApprovalStatus == OperatorApprovalStatus.Approved
                        && tour.OperatorProfile.User.Status == AccountStatus.Active);

                if (destination is not null)
                {
                    tours = tours.Where(tour =>
                        tour.Destinations.Any(link =>
                            link.Destination.Name.Contains(destination)));
                }

                if (request.MinPrice.HasValue)
                {
                    tours = tours.Where(tour => tour.BasePrice >= request.MinPrice.Value);
                }

                if (request.MaxPrice.HasValue)
                {
                    tours = tours.Where(tour => tour.BasePrice <= request.MaxPrice.Value);
                }

                if (hasDepartureDate)
                {
                    tours = tours.Where(tour => tour.Schedules.Any(schedule =>
                        schedule.Status == TourScheduleStatus.Scheduled
                        && schedule.StartAtUtc > now
                        && schedule.EndAtUtc > schedule.StartAtUtc
                        && schedule.StartAtUtc >= lowerInclusive
                        && schedule.StartAtUtc < upperExclusive));
                }

                var totalCount = await tours.LongCountAsync(transactionCancellationToken);

                var rows = await tours
                    .OrderBy(tour => EF.Functions.Collate(
                        tour.Title,
                        Tour.DestinationCollation))
                    .ThenBy(tour => tour.Id)
                    .Skip(offset)
                    .Take(request.PageSize)
                    .SelectMany(
                        tour => tour.Schedules
                            .Where(schedule =>
                                schedule.Status == TourScheduleStatus.Scheduled
                                && schedule.StartAtUtc > now
                                && schedule.EndAtUtc > schedule.StartAtUtc
                                && (!hasDepartureDate
                                    || (schedule.StartAtUtc >= lowerInclusive
                                        && schedule.StartAtUtc < upperExclusive)))
                            .OrderBy(schedule =>
                                schedule.TotalCapacity > 0
                                && schedule.ReservedCapacity >= 0
                                && schedule.ReservedCapacity < schedule.TotalCapacity
                                    ? 0
                                    : 1)
                            .ThenBy(schedule => schedule.StartAtUtc)
                            .ThenBy(schedule => schedule.Id)
                            .Take(1)
                            .DefaultIfEmpty(),
                        (tour, schedule) => new
                        {
                            tour.Id,
                            tour.Title,
                            OperatorName = tour.OperatorProfile.CompanyName,
                            tour.DurationDays,
                            tour.BasePrice,
                            ScheduleId = schedule == null ? null : (long?)schedule.Id,
                            DepartureAtUtc = schedule == null
                                ? null
                                : (DateTimeOffset?)schedule.StartAtUtc,
                            TotalCapacity = schedule == null ? null : (int?)schedule.TotalCapacity,
                            ReservedCapacity = schedule == null
                                ? null
                                : (int?)schedule.ReservedCapacity,
                        })
                    .ToListAsync(transactionCancellationToken);

                var pageTourIds = rows.Select(row => row.Id).ToArray();
                var pageDestinations = await dbContext.TourDestinations
                    .AsNoTracking()
                    .Where(link => pageTourIds.Contains(link.TourId))
                    .OrderBy(link => link.TourId)
                    .ThenBy(link => link.SequenceNo)
                    .Select(link => new { link.TourId, link.Destination.Name })
                    .ToListAsync(transactionCancellationToken);
                var destinationsByTour = pageDestinations
                    .GroupBy(link => link.TourId)
                    .ToDictionary(group => group.Key,
                        group => (IReadOnlyList<string>)group.Select(link => link.Name).ToArray());

                var items = rows.Select(row => MapItem(
                        row.Id,
                        row.Title,
                        destinationsByTour.GetValueOrDefault(row.Id) ?? [],
                        row.OperatorName,
                        row.DurationDays,
                        row.BasePrice,
                        row.ScheduleId,
                        row.DepartureAtUtc,
                        row.TotalCapacity,
                        row.ReservedCapacity))
                    .ToArray();
                var totalPages = totalCount / request.PageSize
                    + (totalCount % request.PageSize == 0 ? 0 : 1);

                return Result.Success(new PagedToursResponseDto(
                    request.Page,
                    request.PageSize,
                    totalCount,
                    totalPages,
                    now.UtcDateTime,
                    items));
            },
            cancellationToken);
    }

    private TourSearchItemDto MapItem(
        long tourId,
        string title,
        IReadOnlyList<string> destinations,
        string operatorName,
        int durationDays,
        decimal basePrice,
        long? scheduleId,
        DateTimeOffset? departureAtUtc,
        int? totalCapacity,
        int? reservedCapacity)
    {
        var hasValidCapacity = totalCapacity is > 0
            && reservedCapacity is >= 0
            && reservedCapacity <= totalCapacity;
        var availability = scheduleId switch
        {
            null => "noUpcomingSchedule",
            _ when !hasValidCapacity => "unknown",
            _ when reservedCapacity < totalCapacity => "available",
            _ => "soldOut",
        };
        var remainingSlots = hasValidCapacity
            ? totalCapacity - reservedCapacity
            : null;

        if (scheduleId.HasValue && !hasValidCapacity)
        {
            logger.LogWarning(
                "TM-70 availability unknown for tour {TourId}: invalid schedule capacity snapshot.",
                tourId);
        }

        var publicScheduleId = availability == "unknown" ? null : scheduleId;
        var publicDepartureAtUtc = availability == "unknown"
            ? null
            : departureAtUtc?.UtcDateTime;

        return new TourSearchItemDto(
            tourId.ToString(CultureInfo.InvariantCulture),
            title,
            destinations,
            operatorName,
            durationDays,
            checked((long)basePrice),
            Currency,
            publicScheduleId?.ToString(CultureInfo.InvariantCulture),
            publicDepartureAtUtc,
            availability,
            remainingSlots);
    }
}
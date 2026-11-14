using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TourRecommendations.Common;
using TripMate.Application.Features.TourRecommendations.Refresh;
using TripMate.Application.Features.Tours.Search;
using TripMate.Application.Features.Tours.Shared;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TourRecommendations.GetLatest;

internal sealed class GetLatestTourRecommendationsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<GetLatestTourRecommendationsQuery, Result<TourRecommendationPageDto>>
{
    private const int PageSize = 20;

    public async Task<Result<TourRecommendationPageDto>> Handle(
        GetLatestTourRecommendationsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } travelerUserId)
        {
            return Result.Failure<TourRecommendationPageDto>(
                TourRecommendationErrorCodes.Unauthorized,
                "Authentication is required.");
        }

        return await dbContext.ExecuteInSerializableTransactionAsync(async _ =>
        {
            var nowUtc = clock.UtcNow.ToUniversalTime();
            var snapshot = await dbContext.TourSearchRequests
                .AsNoTracking()
                .Where(candidate => candidate.TravelerUserId == travelerUserId)
                .OrderByDescending(candidate => candidate.Id)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.DestinationText,
                    candidate.StartDate,
                    candidate.EndDate,
                    candidate.MinBudget,
                    candidate.MaxBudget,
                    candidate.RequestedAtUtc,
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (snapshot is null)
            {
                return Result.Success(EmptyPage(request.Page, nowUtc));
            }

            var publicTourIds = dbContext.Tours
                .AsNoTracking()
                .Where(TourVisibility.PublicTour(nowUtc))
                .Where(tour => tour.Schedules.Any(schedule =>
                    schedule.Status == TourScheduleStatus.Scheduled
                    && schedule.StartAtUtc > nowUtc
                    && schedule.EndAtUtc > schedule.StartAtUtc
                    && (!schedule.RegistrationDeadlineAtUtc.HasValue
                        || nowUtc < schedule.RegistrationDeadlineAtUtc.Value)
                    && schedule.TotalCapacity > 0
                    && schedule.ReservedCapacity >= 0
                    && schedule.ReservedCapacity < schedule.TotalCapacity))
                .Select(tour => tour.Id);

            var eligibleLogs = dbContext.TourRecommendationLogs
                .AsNoTracking()
                .Where(log => log.SearchRequestId == snapshot.Id
                    && log.IsRecommended
                    && publicTourIds.Contains(log.TourId));
            long totalCount = await eligibleLogs.LongCountAsync(cancellationToken);
            long offset = ((long)request.Page - 1L) * PageSize;
            var rows = offset > int.MaxValue
                ? []
                : await eligibleLogs
                    .OrderByDescending(log => log.SimilarityScore)
                    .ThenBy(log => log.TourId)
                    .Skip((int)offset)
                    .Take(PageSize)
                    .Select(log => new
                    {
                        log.TourId,
                        log.SimilarityScore,
                        log.Tour.Title,
                        log.Tour.BasePrice,
                        log.Tour.DurationDays,
                        OperatorName = log.Tour.OperatorProfile.CompanyName,
                        RawAverage = dbContext.TripReviews
                            .Where(review => review.TourId == log.TourId
                                && review.PublicationStatus == TripReview.PublishedStatus)
                            .Select(review => (decimal?)review.OverallRating)
                            .Average(),
                    })
                    .ToListAsync(cancellationToken);

            long[] tourIds = rows.Select(row => row.TourId).ToArray();
            var schedules = tourIds.Length == 0
                ? []
                : await dbContext.TourSchedules
                    .AsNoTracking()
                    .Where(schedule => tourIds.Contains(schedule.TourId)
                        && schedule.Status == TourScheduleStatus.Scheduled
                        && schedule.StartAtUtc > nowUtc
                        && schedule.EndAtUtc > schedule.StartAtUtc
                        && (!schedule.RegistrationDeadlineAtUtc.HasValue
                            || nowUtc < schedule.RegistrationDeadlineAtUtc.Value)
                        && schedule.TotalCapacity > 0
                        && schedule.ReservedCapacity >= 0
                        && schedule.ReservedCapacity < schedule.TotalCapacity)
                    .OrderBy(schedule => schedule.TourId)
                    .ThenBy(schedule => schedule.StartAtUtc)
                    .ThenBy(schedule => schedule.Id)
                    .Select(schedule => new
                    {
                        schedule.TourId,
                        Value = new TourRecommendationCandidateSchedule(
                            schedule.Id,
                            schedule.StartAtUtc,
                            schedule.EndAtUtc,
                            schedule.TotalCapacity,
                            schedule.ReservedCapacity,
                            schedule.RegistrationDeadlineAtUtc,
                            false),
                    })
                    .ToListAsync(cancellationToken);
            var schedulesByTour = schedules
                .GroupBy(schedule => schedule.TourId)
                .ToDictionary(group => group.Key, group => group.Select(schedule => schedule.Value));

            var destinationRows = tourIds.Length == 0
                ? []
                : await dbContext.TourDestinations
                    .AsNoTracking()
                    .Where(link => tourIds.Contains(link.TourId))
                    .OrderBy(link => link.TourId)
                    .ThenBy(link => link.SequenceNo)
                    .Select(link => new { link.TourId, link.Destination.Name })
                    .ToListAsync(cancellationToken);
            var destinationsByTour = destinationRows
                .GroupBy(row => row.TourId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row.Name).ToArray());

            var thumbnails = tourIds.Length == 0
                ? new Dictionary<long, string>()
                : await dbContext.TourMedia
                    .AsNoTracking()
                    .Where(media => tourIds.Contains(media.TourId) && media.IsPrimary)
                    .Select(media => new { media.TourId, media.DeliveryUrl })
                    .ToDictionaryAsync(media => media.TourId, media => media.DeliveryUrl, cancellationToken);

            var items = rows.Select(row =>
            {
                var representative = TourRecommendationCandidateReader.SelectRepresentativeSchedule(
                    schedulesByTour.GetValueOrDefault(row.TourId) ?? [],
                    snapshot.StartDate,
                    snapshot.EndDate)!;
                return new TourRecommendationItemDto(
                    new TourSearchItemDto(
                        row.TourId.ToString(CultureInfo.InvariantCulture),
                        row.Title,
                        destinationsByTour.GetValueOrDefault(row.TourId) ?? [],
                        row.OperatorName,
                        row.DurationDays,
                        checked((long)row.BasePrice),
                        "VND",
                        representative.Id.ToString(CultureInfo.InvariantCulture),
                        representative.StartAtUtc.UtcDateTime,
                        "available",
                        representative.TotalCapacity - representative.ReservedCapacity,
                        thumbnails.GetValueOrDefault(row.TourId),
                        TripReviewAggregateDto.Round(row.RawAverage)),
                    row.SimilarityScore,
                    []);
            }).ToArray();

            return Result.Success(new TourRecommendationPageDto(
                request.Page,
                PageSize,
                totalCount,
                (totalCount + PageSize - 1) / PageSize,
                snapshot.RequestedAtUtc.UtcDateTime,
                new TourRecommendationCriteriaDto(
                    snapshot.DestinationText,
                    snapshot.StartDate,
                    snapshot.EndDate,
                    snapshot.MinBudget,
                    snapshot.MaxBudget),
                items));
        }, cancellationToken);
    }

    private static TourRecommendationPageDto EmptyPage(int page, DateTimeOffset nowUtc) =>
        new(page, PageSize, 0, 0, nowUtc.UtcDateTime, null, []);
}
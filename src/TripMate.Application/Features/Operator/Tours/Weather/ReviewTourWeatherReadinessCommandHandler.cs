using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Weather;

public sealed class ReviewTourWeatherReadinessCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<ReviewTourWeatherReadinessCommand, Result<ReviewTourWeatherReadinessResponse>>
{
    public async Task<Result<ReviewTourWeatherReadinessResponse>> Handle(
        ReviewTourWeatherReadinessCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorId || operatorId <= 0)
            return AccessDenied();
        if (request.TourId <= 0 || request.Stops is null || request.Departures is null)
            return InvalidReview();

        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            bool eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
                profile.UserId == operatorId
                && profile.ApprovalStatus == OperatorApprovalStatus.Approved
                && profile.User.Role == UserRole.TourOperator
                && profile.User.Status == AccountStatus.Active,
                transactionCancellationToken);
            if (!eligible) return AccessDenied();

            Tour? tour = await dbContext.Tours
                .Include(candidate => candidate.ItineraryItems)
                .Include(candidate => candidate.Schedules)
                .SingleOrDefaultAsync(candidate => candidate.Id == request.TourId
                    && candidate.OperatorUserId == operatorId, transactionCancellationToken);
            if (tour is null)
                return Result.Failure<ReviewTourWeatherReadinessResponse>(
                    OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");

            var now = clock.UtcNow;
            var activeDepartures = tour.Schedules
                .Where(schedule => schedule.Status == TourScheduleStatus.Scheduled && schedule.EndAtUtc > now)
                .ToArray();
            if (!ExactlyMatches(request.Stops.Select(stop => stop.TourItemId),
                    tour.ItineraryItems.Select(item => item.Id))
                || !ExactlyMatches(request.Departures.Select(departure => departure.ScheduleId),
                    activeDepartures.Select(schedule => schedule.Id))
                || request.Departures.Any(departure =>
                    string.IsNullOrWhiteSpace(departure.MeetingPoint)
                    || departure.MeetingPoint.Trim().Length > TourSchedule.MeetingPointMaxLength
                    || departure.Latitude is < -90 or > 90
                    || departure.Longitude is < -180 or > 180))
                return InvalidReview();

            var stopReviews = request.Stops.ToDictionary(review => review.TourItemId);
            foreach (var item in tour.ItineraryItems)
                item.ClassifyForWeather(stopReviews[item.Id].Required);

            var departureReviews = request.Departures.ToDictionary(review => review.ScheduleId);
            foreach (var schedule in activeDepartures)
            {
                var review = departureReviews[schedule.Id];
                schedule.ReviewWeatherMeetingPoint(review.MeetingPoint, review.Latitude, review.Longitude);
            }

            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorId,
                AuditActionTypes.TourWeatherReadinessReviewed, AuditEntityTypes.Tour,
                tour.Id, now, AuditOutcome.Success,
                afterData: JsonSerializer.Serialize(new
                {
                    stopIds = stopReviews.Keys.Order().ToArray(),
                    scheduleIds = departureReviews.Keys.Order().ToArray(),
                })));
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return Result.Success(new ReviewTourWeatherReadinessResponse(
                tour.Id, stopReviews.Count, departureReviews.Count));
        }, cancellationToken);
    }

    private static bool ExactlyMatches(IEnumerable<long> suppliedIds, IEnumerable<long> actualIds)
    {
        var supplied = suppliedIds.ToArray();
        var actual = actualIds.ToArray();
        return supplied.Length == actual.Length && supplied.Distinct().Count() == supplied.Length
            && supplied.Order().SequenceEqual(actual.Order());
    }

    private static Result<ReviewTourWeatherReadinessResponse> AccessDenied() =>
        Result.Failure<ReviewTourWeatherReadinessResponse>(OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");

    private static Result<ReviewTourWeatherReadinessResponse> InvalidReview() =>
        Result.Failure<ReviewTourWeatherReadinessResponse>(OperatorTourErrorCodes.WeatherReviewInvalid,
            "Review every stop and unfinished departure with valid meeting coordinates.");
}

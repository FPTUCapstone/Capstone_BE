using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Get;

public sealed class GetOperatorTourQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<GetOperatorTourQuery, Result<OperatorTourDetailDto>>
{
    public async Task<Result<OperatorTourDetailDto>> Handle(
        GetOperatorTourQuery request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue)
        {
            return AccessDenied();
        }

        long operatorId = currentUser.UserId.Value;
        bool eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId &&
            profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            profile.User.Role == UserRole.TourOperator &&
            profile.User.Status == AccountStatus.Active,
            cancellationToken);
        if (!eligible)
        {
            return AccessDenied();
        }

        var tour = await dbContext.Tours.AsNoTracking()
            .Where(candidate => candidate.Id == request.TourId && candidate.OperatorUserId == operatorId)
            .Select(candidate => new OperatorTourDetailDto(
                candidate.Id,
                candidate.Title,
                candidate.DestinationSummary,
                candidate.Description!,
                candidate.CategoryId,
                candidate.BasePrice,
                candidate.DurationDays,
                candidate.Status.ToString(),
                candidate.RejectionReason,
                candidate.Inclusions,
                candidate.Exclusions,
                candidate.Destinations
                    .OrderBy(link => link.SequenceNo)
                    .Select(link => new OperatorTourDestinationDto(link.DestinationId, link.Destination.Name))
                    .ToList(),
                candidate.CancellationPolicyTemplateId,
                candidate.CancellationPolicyRules
                    .OrderByDescending(rule => rule.DaysBeforeDeparture)
                    .Select(rule => new OperatorTourCancellationRuleDto(
                        rule.DaysBeforeDeparture, rule.CancellationFeePercent))
                    .ToList(),
                candidate.ItineraryItems
                    .OrderBy(item => item.DayNo)
                    .ThenBy(item => item.SequenceNo)
                    .Select(item => new OperatorTourItineraryItemDto(
                        item.DayNo, item.SequenceNo, item.PoiId, item.PlannedTime,
                        item.StayDurationMinutes, item.Notes))
                    .ToList(),
                candidate.Schedules
                    .OrderBy(schedule => schedule.StartAtUtc)
                    .ThenBy(schedule => schedule.Id)
                    .Select(schedule => new OperatorTourDepartureDto(
                        schedule.Id, schedule.StartAtUtc, schedule.EndAtUtc,
                        schedule.MeetingPoint, schedule.MinimumCapacity,
                        schedule.TotalCapacity, schedule.ReservedCapacity,
                        schedule.RegistrationDeadlineAtUtc, schedule.AdultPrice,
                        schedule.ChildPrice, schedule.InfantPrice,
                        schedule.Status.ToString()))
                    .ToList()))
            .SingleOrDefaultAsync(cancellationToken);

        if (tour is null)
        {
            return Result.Failure<OperatorTourDetailDto>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
        }

        // Same evaluator as submit-review, so the hint shown to the operator matches what the command enforces.
        var terms = await dbContext.Tours.AsNoTracking()
            .Where(candidate => candidate.Id == tour.Id)
            .Select(candidate => new { candidate.TimeZoneId, candidate.Highlights, candidate.TravellerRequirements })
            .SingleAsync(cancellationToken);
        string timeZoneId = string.IsNullOrWhiteSpace(terms.TimeZoneId) ? Tour.DefaultTimeZoneId : terms.TimeZoneId;
        var warnings = await BuildWarningsAsync(tour, timeZoneId, cancellationToken);
        var readiness = await TourReviewReadinessEvaluator.LoadAsync(
            dbContext, tour.Id, tour.CancellationPolicyTemplateId, clock.UtcNow, cancellationToken);
        bool editable = Enum.TryParse(tour.Status, out TourStatus status) && TourReviewReadinessEvaluator.IsEditable(status);
        return Result.Success(tour with
        {
            MediaCount = readiness.MediaCount,
            HasPrimaryImage = readiness.HasPrimaryImage,
            ReviewBlockers = readiness.Blockers,
            CanSubmitForReview = readiness.IsReady && editable,
            Warnings = warnings,
            TimeZoneId = timeZoneId,
            Highlights = terms.Highlights,
            TravellerRequirements = terms.TravellerRequirements,
        });
    }

    private async Task<IReadOnlyList<OperatorTourWarningDto>> BuildWarningsAsync(
        OperatorTourDetailDto tour, string timeZoneId, CancellationToken cancellationToken)
    {
        var firstDeparture = tour.Departures
            .Where(departure => departure.Status == nameof(TourScheduleStatus.Scheduled))
            .OrderBy(departure => departure.StartAtUtc)
            .FirstOrDefault();
        DateOnly? firstLocalDate = null;
        if (firstDeparture is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone))
        {
            firstLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(firstDeparture.StartAtUtc, zone).DateTime);
        }

        long[] poiIds = tour.ItineraryItems.Select(item => item.PoiId).Distinct().ToArray();
        var hours = await dbContext.PoiOpeningHours.AsNoTracking()
            .Where(candidate => poiIds.Contains(candidate.PointOfInterestId))
            .Select(candidate => new PoiOpeningHoursFact(
                candidate.PointOfInterestId, candidate.DayOfWeek, candidate.OpenTime, candidate.CloseTime, candidate.IsClosed))
            .ToListAsync(cancellationToken);
        return TourItineraryWarningEvaluator.Evaluate(
            tour.ItineraryItems
                .Select(item => new TourItineraryItemFact(
                    item.DayNo, item.SequenceNo, item.PoiId, item.PlannedTime, item.StayDurationMinutes))
                .ToList(),
            firstLocalDate,
            hours);
    }

    private static Result<OperatorTourDetailDto> AccessDenied() =>
        Result.Failure<OperatorTourDetailDto>(OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}
using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Tours.Review.PendingTours;

public sealed class GetPendingTourReviewQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetPendingTourReviewQuery, Result<PendingTourReviewDto>>
{
    private static readonly string[] LineBreaks = ["\r\n", "\n", "\r"];

    public async Task<Result<PendingTourReviewDto>> Handle(
        GetPendingTourReviewQuery request, CancellationToken cancellationToken)
    {
        if (!await TourReviewReadAccess.IsActiveStaffAsync(dbContext, currentUser, cancellationToken))
        {
            return Result.Failure<PendingTourReviewDto>(
                OperatorTourErrorCodes.AdminAccessRequired, "Staff access is required.");
        }

        var status = await dbContext.Tours.AsNoTracking()
            .Where(tour => tour.Id == request.TourId)
            .Select(tour => (TourStatus?)tour.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (status is null)
        {
            return Result.Failure<PendingTourReviewDto>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
        }

        if (status != TourStatus.Pending)
        {
            return Result.Failure<PendingTourReviewDto>(
                OperatorTourErrorCodes.InvalidState,
                "The selected package is no longer in the Pending Approval status.");
        }

        var row = await dbContext.Tours.AsNoTracking()
            .Where(tour => tour.Id == request.TourId)
            .Select(tour => new
            {
                tour.Id,
                tour.Title,
                OperatorName = tour.OperatorProfile.CompanyName,
                tour.DestinationSummary,
                Category = tour.Category != null ? tour.Category.Name : null,
                SubmittedAtUtc = tour.SubmittedAtUtc ?? tour.UpdatedAtUtc, // legacy rows pre-date the column
                tour.SubmissionNote,
                tour.DurationDays,
                tour.BasePrice,
                tour.Description,
                tour.Highlights,
                tour.Inclusions,
                tour.Exclusions,
                tour.TravellerRequirements,
                tour.AllowDeposit,
                tour.DepositPercent,
                tour.BalanceDueDaysBeforeDeparture,
                Policy = tour.CancellationPolicyRules
                    .OrderByDescending(rule => rule.DaysBeforeDeparture)
                    .Select(rule => new PendingTourCancellationRuleDto(rule.DaysBeforeDeparture, rule.CancellationFeePercent))
                    .ToList(),
                Schedules = tour.Schedules
                    .OrderBy(schedule => schedule.StartAtUtc)
                    .Select(schedule => new PendingTourScheduleDto(
                        schedule.StartAtUtc, schedule.EndAtUtc, schedule.MeetingPoint, schedule.TotalCapacity,
                        schedule.AdultPrice, schedule.ChildPrice, schedule.InfantPrice))
                    .ToList(),
                Itinerary = tour.ItineraryItems
                    .OrderBy(item => item.DayNo)
                    .ThenBy(item => item.SequenceNo)
                    .Select(item => new PendingTourItineraryStopDto(
                        item.DayNo, item.SequenceNo, item.Poi.Name, item.PlannedTime, item.StayDurationMinutes, item.Notes))
                    .ToList(),
                Images = tour.Media
                    .Where(media => media.LifecycleStatus == TourMediaLifecycleStatus.Active && media.DeletedAtUtc == null)
                    .OrderBy(media => media.SortOrder)
                    .Select(media => new PendingTourImageDto(media.DeliveryUrl, media.AltText, media.Caption, media.IsPrimary))
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            // Deleted between the status check and the read.
            return Result.Failure<PendingTourReviewDto>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
        }

        var first = row.Schedules.FirstOrDefault();
        return Result.Success(new PendingTourReviewDto(
            row.Id,
            row.Title,
            row.OperatorName,
            row.DestinationSummary,
            row.Category,
            row.SubmittedAtUtc,
            row.DurationDays,
            row.BasePrice,
            first?.StartAtUtc,
            first?.Capacity,
            row.Images.Count,
            row.Description,
            row.Highlights,
            SplitLines(row.Inclusions),
            SplitLines(row.Exclusions),
            row.TravellerRequirements,
            row.Policy,
            row.Schedules,
            row.Itinerary,
            row.Images)
        {
            SubmissionNote = row.SubmissionNote,
            AllowDeposit = row.AllowDeposit,
            DepositPercent = row.DepositPercent,
            BalanceDueDaysBeforeDeparture = row.BalanceDueDaysBeforeDeparture,
        });
    }

    private static IReadOnlyList<string> SplitLines(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
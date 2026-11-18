using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Common;

/// <summary>Stable blocker codes returned to clients; clients may map them to localized copy.</summary>
public static class TourReviewBlockers
{
    public const string RequiredFieldsMissing = "required_fields_missing";
    public const string ItineraryMissing = "itinerary_missing";
    public const string PrimaryImageMissing = "primary_image_missing";
    public const string FutureDepartureMissing = "future_departure_missing";
    public const string CancellationPolicyMissing = "cancellation_policy_missing";
    public const string DestinationMissing = "destination_missing";
}

/// <summary>Facts about a package that decide whether it may be sent for review.</summary>
public sealed record TourReviewFacts(
    bool HasPrimaryImage,
    bool HasFuturePricedDeparture,
    bool HasCancellationPolicySnapshot,
    bool HasDestination,
    bool HasRequiredFields = true,
    bool HasItinerary = true);

/// <summary>
/// The single source of truth for the review prerequisites. The detail query and
/// <c>submit-review</c> both use it, so the UI hint can never disagree with the command.
/// </summary>
public static class TourReviewReadinessEvaluator
{
    public static IReadOnlyList<string> Evaluate(TourReviewFacts facts)
    {
        var blockers = new List<string>(6);
        if (!facts.HasRequiredFields) blockers.Add(TourReviewBlockers.RequiredFieldsMissing);
        if (!facts.HasItinerary) blockers.Add(TourReviewBlockers.ItineraryMissing);
        if (!facts.HasPrimaryImage) blockers.Add(TourReviewBlockers.PrimaryImageMissing);
        if (!facts.HasFuturePricedDeparture) blockers.Add(TourReviewBlockers.FutureDepartureMissing);
        if (!facts.HasCancellationPolicySnapshot) blockers.Add(TourReviewBlockers.CancellationPolicyMissing);
        if (!facts.HasDestination) blockers.Add(TourReviewBlockers.DestinationMissing);
        return blockers;
    }

    public static bool IsEditable(TourStatus status) => status is TourStatus.Draft or TourStatus.Rejected;

    /// <summary>Reads the facts for one tour from the database and evaluates them at <paramref name="nowUtc"/>.</summary>
    public static async Task<TourReviewReadiness> LoadAsync(
        IApplicationDbContext dbContext,
        long tourId,
        long? cancellationPolicyTemplateId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var activeMedia = dbContext.TourMedia.Where(media =>
            media.TourId == tourId && media.LifecycleStatus == TourMediaLifecycleStatus.Active);
        int mediaCount = await activeMedia.CountAsync(cancellationToken);
        bool hasPrimaryImage = await activeMedia.AnyAsync(media => media.IsPrimary, cancellationToken);
        var tour = await dbContext.Tours.AsNoTracking().SingleAsync(candidate => candidate.Id == tourId, cancellationToken);
        bool hasRequiredFields = !string.IsNullOrWhiteSpace(tour.Title) && tour.Title.Trim().Length <= Tour.TitleMaxLength
            && !string.IsNullOrWhiteSpace(tour.Description) && tour.DurationDays > 0
            && tour.BasePrice > 0 && tour.BasePrice <= 9_999_999_999.99m && OperatorTourValidation.IsWholeVnd(tour.BasePrice)
            && (!tour.CategoryId.HasValue || tour.CategoryId > 0)
            && (string.IsNullOrWhiteSpace(tour.TimeZoneId) || Tour.IsKnownTimeZone(tour.TimeZoneId))
            && OperatorTourValidation.IsWithinTermsLimit(tour.Highlights)
            && OperatorTourValidation.IsWithinTermsLimit(tour.Inclusions)
            && OperatorTourValidation.IsWithinTermsLimit(tour.Exclusions)
            && OperatorTourValidation.IsWithinTermsLimit(tour.TravellerRequirements);
        var items = await dbContext.TourItineraryItems.AsNoTracking()
            .Where(item => item.TourId == tourId).ToListAsync(cancellationToken);
        bool hasItinerary = items.Count > 0 && items.All(item => item.DayNo > 0 && item.DayNo <= tour.DurationDays
            && item.SequenceNo > 0 && item.PoiId > 0 && item.StayDurationMinutes > 0
            && (item.Notes is null || item.Notes.Length <= TourItineraryItem.NotesMaxLength))
            && items.Select(item => (item.DayNo, item.SequenceNo)).Distinct().Count() == items.Count;
        var departures = await dbContext.TourSchedules.AsNoTracking()
            .Where(schedule => schedule.TourId == tourId && schedule.StartAtUtc > nowUtc
                && schedule.Status == TourScheduleStatus.Scheduled).ToListAsync(cancellationToken);
        bool hasFutureDeparture = departures.Any(schedule => schedule.EndAtUtc > schedule.StartAtUtc
            && schedule.TotalCapacity > 0 && schedule.MinimumCapacity > 0 && schedule.MinimumCapacity <= schedule.TotalCapacity
            && (!schedule.RegistrationDeadlineAtUtc.HasValue || schedule.RegistrationDeadlineAtUtc < schedule.StartAtUtc)
            && IsValidPrice(schedule.AdultPrice ?? tour.BasePrice, positive: true)
            && (!schedule.ChildPrice.HasValue || IsValidPrice(schedule.ChildPrice.Value, positive: false))
            && (!schedule.InfantPrice.HasValue || IsValidPrice(schedule.InfantPrice.Value, positive: false))
            && (schedule.MeetingPoint is null || schedule.MeetingPoint.Length <= TourSchedule.MeetingPointMaxLength)
            && ((!schedule.MeetingLatitude.HasValue && !schedule.MeetingLongitude.HasValue)
                || (!string.IsNullOrWhiteSpace(schedule.MeetingPoint) && schedule.MeetingLatitude is >= -90 and <= 90
                    && schedule.MeetingLongitude is >= -180 and <= 180)));
        bool hasDestination = await dbContext.TourDestinations.AnyAsync(link => link.TourId == tourId,
            cancellationToken);
        bool hasPolicySnapshot = cancellationPolicyTemplateId.HasValue &&
            await dbContext.TourCancellationPolicyRules.AnyAsync(rule => rule.TourId == tourId,
                cancellationToken);

        var blockers = Evaluate(new TourReviewFacts(hasPrimaryImage, hasFutureDeparture, hasPolicySnapshot, hasDestination, hasRequiredFields, hasItinerary));
        return new TourReviewReadiness(mediaCount, hasPrimaryImage, blockers);
    }
    private static bool IsValidPrice(decimal price, bool positive) =>
        (positive ? price > 0 : price >= 0) && price <= 9_999_999_999.99m && OperatorTourValidation.IsWholeVnd(price);
}

public sealed record TourReviewReadiness(int MediaCount, bool HasPrimaryImage, IReadOnlyList<string> Blockers)
{
    public bool IsReady => Blockers.Count == 0;
}
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Submit;

namespace TripMate.Application.Features.TripReviews.Common;

/// <summary>
/// Optional-field checks against an already-authorized server context, never a
/// client-supplied capability. This is not authorization or a submit/duplicate
/// guard; Tasks 9/10 own ordering and Task 9 rechecks evidence under the write lock.
/// </summary>
public static class TripReviewInputEligibility
{
    public static Result Validate(SubmitTripReviewCommand command, TripReviewContextDto ownedContext)
    {
        var contextIdentity = ownedContext.ReviewableRecord
            ?? ReviewableRecordRef.CommerceBooking(ownedContext.ParentId);
        if (command.ReviewableRecord != contextIdentity)
            return Result.Failure(TripReviewErrorCodes.BookingNotFound, "Booking not found.");
        if (!new SubmitTripReviewCommandValidator().Validate(command).IsValid)
            return Result.Failure(TripReviewErrorCodes.InvalidInput, "Invalid review input.");
        if (command.RoutePacing.HasValue && !ownedContext.RoutePacing.Available)
            return Result.Failure(TripReviewErrorCodes.RoutePacingUnavailable, "Route pacing is unavailable for this booking.");
        if (command.CspRating.HasValue && !ownedContext.CspRating.Available)
            return Result.Failure(TripReviewErrorCodes.CspIneligible, "CSP rating is unavailable for this booking.");
        if (command.PoiRatings!.Count > 0)
        {
            if (!ownedContext.PoiRatings.Available)
                return Result.Failure(TripReviewErrorCodes.PoiUnavailable, "POI ratings are unavailable for this booking.");
            var eligibleIds = ownedContext.EligiblePois.Select(p => p.PoiId).ToHashSet();
            if (command.PoiRatings.Any(p => !eligibleIds.Contains(p.PoiId)))
                return Result.Failure(TripReviewErrorCodes.PoiIneligible, "A POI is not eligible for this booking.");
        }
        return Result.Success();
    }
}
using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripReviews.Submit;

/// <summary>BookingId comes from the route; author and eligibility are never supplied by the client.</summary>
public sealed record SubmitTripReviewCommand : IRequest<Result<NewTripReviewDto>>
{
    public SubmitTripReviewCommand(long bookingId, int overallRating, string? title, string? content,
        IReadOnlyList<TripReviewPoiInput>? poiRatings, IReadOnlyList<TripReviewPhotoInput>? photos,
        RoutePacingFeedback? routePacing = null, int? cspRating = null, bool publishDisplayName = false)
        : this(ReviewableRecordRef.UnvalidatedCommerceBooking(bookingId), overallRating, title, content, poiRatings, photos,
            routePacing, cspRating, publishDisplayName)
    { }

    public SubmitTripReviewCommand(ReviewableRecordRef reviewableRecord, int overallRating, string? title,
        string? content, IReadOnlyList<TripReviewPoiInput>? poiRatings,
        IReadOnlyList<TripReviewPhotoInput>? photos, RoutePacingFeedback? routePacing = null,
        int? cspRating = null, bool publishDisplayName = false)
    {
        ReviewableRecord = reviewableRecord ?? throw new ArgumentNullException(nameof(reviewableRecord));
        OverallRating = overallRating;
        Text = ReviewText.Normalize(title, content);
        PoiRatings = poiRatings is null ? null : Array.AsReadOnly(poiRatings.ToArray());
        Photos = photos is null ? null : Array.AsReadOnly(photos.ToArray());
        RoutePacing = routePacing;
        CspRating = cspRating;
        PublishDisplayName = publishDisplayName;
    }

    public ReviewableRecordRef ReviewableRecord { get; }
    public long BookingId => ReviewableRecord.Id;
    public int OverallRating { get; }
    public ReviewText Text { get; }
    public IReadOnlyList<TripReviewPoiInput>? PoiRatings { get; }
    public IReadOnlyList<TripReviewPhotoInput>? Photos { get; }
    public RoutePacingFeedback? RoutePacing { get; }
    public int? CspRating { get; }
    public bool PublishDisplayName { get; }
}
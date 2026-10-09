using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Application.Features.TripReviews.Edit;

public sealed record EditTripReviewCommand : IRequest<Result<NewTripReviewDto>>
{
    public EditTripReviewCommand(long bookingId, int overallRating, string? title, string? content, bool publishDisplayName, string? version)
        : this(ReviewableRecordRef.UnvalidatedCommerceBooking(bookingId), overallRating, title, content,
            publishDisplayName, version)
    { }

    public EditTripReviewCommand(ReviewableRecordRef reviewableRecord, int overallRating, string? title,
        string? content, bool publishDisplayName, string? version)
    {
        ReviewableRecord = reviewableRecord ?? throw new ArgumentNullException(nameof(reviewableRecord));
        OverallRating = overallRating;
        Text = ReviewText.Normalize(title, content);
        PublishDisplayName = publishDisplayName;
        Version = version;
    }

    public ReviewableRecordRef ReviewableRecord { get; }
    public long BookingId => ReviewableRecord.Id;
    public int OverallRating { get; }
    public ReviewText Text { get; }
    public bool PublishDisplayName { get; }
    public string? Version { get; }
}
namespace TripMate.Domain.Entities;

public sealed class TripReviewMedia
{
    private TripReviewMedia() { }
    public long Id { get; private set; }
    public long TripReviewId { get; private set; }
    public TripReview Review { get; private set; } = null!;
    public Guid OperationId { get; private set; }
    public TripReviewMediaOperation Operation { get; private set; } = null!;
    public byte SortOrder { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public static TripReviewMedia Link(TripReview parent, TripReviewMediaOperation operation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parent); ArgumentNullException.ThrowIfNull(operation);
        if (operation.State != TripReviewMediaOperation.Adopted
            || parent.BookingId != operation.BookingId
            || parent.ServiceBookingId != operation.ServiceBookingId
            || parent.TravelerUserId != operation.TravelerUserId
            || parent.PublicationStatus != TripReview.PublishedStatus)
            throw new ArgumentException("Media must belong to this published review's typed parent and owner.");
        return new() { Review = parent, Operation = operation, SortOrder = operation.SortOrder, CreatedAtUtc = now.ToUniversalTime() };
    }
}
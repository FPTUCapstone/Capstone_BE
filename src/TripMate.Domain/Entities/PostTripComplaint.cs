using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class PostTripComplaint : BaseEntity
{
    public const int ReasonDetailMaxLength = 2000;

    private PostTripComplaint() { }

    public long BookingId { get; private set; }
    public long TravelerUserId { get; private set; }
    public PostTripComplaintStatus Status { get; private set; }
    public PostTripComplaintReasonCategory ReasonCategory { get; private set; }
    public string ReasonDetail { get; private set; } = string.Empty;
    public DateTimeOffset SubmittedAtUtc { get; private set; }
    public DateTimeOffset ResponseDeadlineUtc { get; private set; }
    public DateTimeOffset? TravelerResponseDeadlineUtc { get; private set; }
    public PostTripComplaintResponseAction? ResponseAction { get; private set; }
    public decimal? ProposedRefundAmount { get; private set; }
    public string? RejectionReason { get; private set; }
    public DateTimeOffset? RespondedAtUtc { get; private set; }
    public long? RespondedByOperatorUserId { get; private set; }
    public decimal RefundableAmount { get; private set; }
    public long? RefundId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static PostTripComplaint Create(long bookingId, long travelerUserId,
        PostTripComplaintReasonCategory reasonCategory, string reasonDetail,
        DateTimeOffset submittedAtUtc, DateTimeOffset responseDeadlineUtc,
        decimal refundableAmount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bookingId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonDetail);
        string normalizedReason = reasonDetail.Trim();
        if (normalizedReason.Length > ReasonDetailMaxLength) throw new ArgumentOutOfRangeException(nameof(reasonDetail));
        if (refundableAmount < 0m) throw new ArgumentOutOfRangeException(nameof(refundableAmount));
        DateTimeOffset submittedAt = submittedAtUtc.ToUniversalTime();
        DateTimeOffset deadline = responseDeadlineUtc.ToUniversalTime();
        if (deadline <= submittedAt) throw new ArgumentOutOfRangeException(nameof(responseDeadlineUtc));
        return new PostTripComplaint
        {
            BookingId = bookingId, TravelerUserId = travelerUserId,
            Status = PostTripComplaintStatus.AwaitingOperatorResponse,
            ReasonCategory = reasonCategory, ReasonDetail = normalizedReason,
            SubmittedAtUtc = submittedAt, ResponseDeadlineUtc = deadline,
            RefundableAmount = refundableAmount,
        };
    }
}

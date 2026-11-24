namespace TripMate.Domain.Entities;

/// <summary>Durable in-app delivery work for a single approved Tour version.</summary>
public sealed class TourApprovalNotice
{
    private TourApprovalNotice()
    {
    }

    public TourApprovalNotice(long tourId, long operatorUserId, string tourTitle, DateTimeOffset approvedAtUtc)
    {
        TourId = tourId;
        OperatorUserId = operatorUserId;
        TourTitle = tourTitle;
        ApprovedAtUtc = approvedAtUtc.ToUniversalTime();
        NextAttemptAtUtc = ApprovedAtUtc;
    }

    public long TourId { get; private set; }
    public long OperatorUserId { get; private set; }
    public string TourTitle { get; private set; } = string.Empty;
    public DateTimeOffset ApprovedAtUtc { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
}
using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class StayOwnershipClaim : BaseEntity
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    private StayOwnershipClaim() { }

    public StayOwnershipClaim(long poiId, long applicantId, string evidence, DateTimeOffset submittedAtUtc)
    {
        PoiId = poiId;
        ApplicantId = applicantId;
        Evidence = evidence;
        SubmittedAtUtc = submittedAtUtc;
    }

    public long PoiId { get; private set; }
    public long ApplicantId { get; private set; }
    public string Evidence { get; private set; } = string.Empty;
    public string Status { get; private set; } = Pending;
    public DateTimeOffset SubmittedAtUtc { get; private set; }
    public long? ReviewerId { get; private set; }
    public string? ReviewNote { get; private set; }

    public void Review(long reviewerId, bool approved, string note)
    {
        if (Status != Pending) throw new InvalidOperationException("Only pending claims can be reviewed.");
        Status = approved ? Approved : Rejected;
        ReviewerId = reviewerId;
        ReviewNote = note;
    }
}

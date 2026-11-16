using TripMate.Domain.Enums;

namespace TripMate.Domain.Common;

/// <summary>UC-75 response window of a partial refund proposal (BR-153).</summary>
public static class PartialRefundProposalRules
{
    /// <summary>BR-153 "3 days" is calendar time, not working days.</summary>
    public static readonly TimeSpan ResponseWindow = TimeSpan.FromHours(72);

    /// <summary>The deadline UC-69 sets when the Tour Operator proposes a partial refund.</summary>
    public static DateTimeOffset DeadlineFrom(DateTimeOffset proposedAtUtc) =>
        proposedAtUtc.ToUniversalTime().Add(ResponseWindow);

    /// <summary>
    /// True only while the Traveler may still answer. The deadline itself is already closed, and a
    /// passed deadline closes the proposal even before the hourly deadline job has declined it.
    /// </summary>
    public static bool IsOpen(PostTripComplaintStatus status, DateTimeOffset responseDeadlineUtc,
        DateTimeOffset nowUtc) =>
        status == PostTripComplaintStatus.AwaitingTravelerResponse && nowUtc < responseDeadlineUtc;
}
using System.Globalization;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.PartialRefundProposals;

/// <summary>MSG166–MSG168 (UC-75 decision record G3).</summary>
internal static class PartialRefundProposalNotifications
{
    private const string Title = "Partial refund proposal";

    public static Notification Accepted(PartialRefundProposal proposal, DateTimeOffset nowUtc) =>
        Create(proposal.TourOperatorUserId, Notification.PartialRefundProposalAcceptedType, proposal, nowUtc,
            string.Create(CultureInfo.InvariantCulture,
                $"The traveler accepted your partial refund proposal of {proposal.ProposedAmount:N0} VND for booking {proposal.BookingCode}. Please confirm the refund request."));

    public static Notification Declined(PartialRefundProposal proposal, DateTimeOffset nowUtc) =>
        Create(proposal.TourOperatorUserId, Notification.PartialRefundProposalDeclinedType, proposal, nowUtc,
            $"The traveler declined your partial refund proposal for booking {proposal.BookingCode}. The complaint will be handled under the partnership contract.");

    public static IEnumerable<Notification> Expired(PartialRefundProposal proposal, DateTimeOffset nowUtc)
    {
        string body = $"The partial refund proposal for booking {proposal.BookingCode} received no response within 3 days and has been closed as declined. The complaint will be handled under the partnership contract.";
        yield return Create(proposal.TravelerUserId, Notification.PartialRefundProposalExpiredType, proposal, nowUtc, body);
        yield return Create(proposal.TourOperatorUserId, Notification.PartialRefundProposalExpiredType, proposal, nowUtc, body);
    }

    private static Notification Create(long userId, string type, PartialRefundProposal proposal,
        DateTimeOffset nowUtc, string body) => new()
        {
            UserId = userId,
            Channel = NotificationChannel.Push,
            Type = type,
            Title = Title,
            Body = body,
            RelatedEntityType = Notification.PostTripComplaintRelatedEntityType,
            RelatedEntityId = proposal.ComplaintId,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = nowUtc,
        };
}
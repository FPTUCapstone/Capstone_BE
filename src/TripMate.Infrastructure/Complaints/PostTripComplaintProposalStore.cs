using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Complaints;

/// <summary>
/// EF Core implementation of <see cref="IPartialRefundProposalStore"/> and <see cref="IComplaintRefundRequestCreator"/>
/// backed by the PostTripComplaint and Refund aggregates (UC-68, UC-69, UC-75).
/// </summary>
internal sealed class PostTripComplaintProposalStore(IApplicationDbContext dbContext) : IPartialRefundProposalStore
{
    public async Task<PartialRefundProposal?> FindAsync(long complaintId, CancellationToken cancellationToken)
    {
        if (complaintId <= 0) return null;

        var row = await (
            from c in dbContext.PostTripComplaints
            where c.Id == complaintId
            join b in dbContext.Bookings on c.BookingId equals b.Id
            join s in dbContext.TourSchedules on b.TourScheduleId equals s.Id
            join t in dbContext.Tours on s.TourId equals t.Id
            select new
            {
                Complaint = c,
                BookingCode = b.BookingCode,
                OperatorUserId = t.OperatorUserId,
            }
        ).FirstOrDefaultAsync(cancellationToken);

        if (row is null) return null;

        return new PartialRefundProposal(
            row.Complaint.Id,
            row.Complaint.BookingId,
            row.BookingCode,
            row.Complaint.TravelerUserId,
            row.OperatorUserId,
            row.Complaint.Status,
            row.Complaint.ProposedRefundAmount ?? 0m,
            row.Complaint.TravelerResponseDeadlineUtc ?? row.Complaint.ResponseDeadlineUtc,
            row.Complaint.PartialRefundProposalReason,
            row.Complaint.PartialRefundProposalRound,
            row.Complaint.OperatorReconsiderationDeadlineUtc,
            row.Complaint.RefundableAmount);
    }

    public async Task<bool> TryCloseAsync(long complaintId, PostTripComplaintStatus outcome, DateTimeOffset respondedAtUtc,
        CancellationToken cancellationToken)
    {
        if (complaintId <= 0) return false;

        var complaint = await dbContext.PostTripComplaints
            .FirstOrDefaultAsync(c => c.Id == complaintId, cancellationToken);

        if (complaint is null) return false;

        return complaint.TryCloseFromTravelerResponse(outcome);
    }

    public async Task<bool> TryCounterAsync(long complaintId, long travelerUserId, decimal requestedAmount, string reason,
        DateTimeOffset counteredAtUtc, DateTimeOffset operatorReconsiderationDeadlineUtc,
        CancellationToken cancellationToken)
    {
        var complaint = await dbContext.PostTripComplaints.SingleOrDefaultAsync(c => c.Id == complaintId, cancellationToken);
        if (complaint is null || complaint.Status != PostTripComplaintStatus.AwaitingTravelerResponse ||
            complaint.PartialRefundProposalRound != 1) return false;
        complaint.CounterPartialRefund(travelerUserId, requestedAmount, reason, counteredAtUtc,
            operatorReconsiderationDeadlineUtc);
        return true;
    }

    public async Task<bool> TryDeclineFinallyAsync(long complaintId, long travelerUserId, string reason,
        DateTimeOffset declinedAtUtc, CancellationToken cancellationToken)
    {
        var complaint = await dbContext.PostTripComplaints.SingleOrDefaultAsync(c => c.Id == complaintId, cancellationToken);
        if (complaint is null || complaint.Status != PostTripComplaintStatus.AwaitingTravelerResponse ||
            complaint.PartialRefundProposalRound != 2) return false;
        complaint.DeclineFinalPartialRefund(travelerUserId, reason);
        return true;
    }

    public async Task<IReadOnlyList<PartialRefundProposal>> ListOverdueAsync(DateTimeOffset nowUtc, int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0) return [];

        var query = from c in dbContext.PostTripComplaints
                    where c.Status == PostTripComplaintStatus.AwaitingTravelerResponse
                          && c.TravelerResponseDeadlineUtc != null
                          && c.TravelerResponseDeadlineUtc <= nowUtc
                    join b in dbContext.Bookings on c.BookingId equals b.Id
                    join s in dbContext.TourSchedules on b.TourScheduleId equals s.Id
                    join t in dbContext.Tours on s.TourId equals t.Id
                    orderby c.TravelerResponseDeadlineUtc
                    select new PartialRefundProposal(
                        c.Id,
                        c.BookingId,
                        b.BookingCode,
                        c.TravelerUserId,
                        t.OperatorUserId,
                        c.Status,
                        c.ProposedRefundAmount ?? 0m,
                        c.TravelerResponseDeadlineUtc!.Value,
                        c.PartialRefundProposalReason,
                        c.PartialRefundProposalRound,
                        c.OperatorReconsiderationDeadlineUtc,
                        c.RefundableAmount);

        return await query.Take(maxCount).ToListAsync(cancellationToken);
    }
}

internal sealed class PostTripComplaintRefundRequestCreator(IApplicationDbContext dbContext) : IComplaintRefundRequestCreator
{
    public async Task CreateForAcceptedComplaintAsync(long complaintId, long bookingId, decimal amount,
        DateTimeOffset requestedAtUtc, CancellationToken cancellationToken)
    {
        var payment = await dbContext.PaymentTransactions
            .Where(p => p.BookingId == bookingId && p.TransactionType == PaymentTransaction.TypePayment &&
                p.Status == PaymentTransactionStatus.Success)
            .OrderByDescending(p => p.SucceededAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (payment is null)
        {
            throw new InvalidOperationException($"No verified payment transaction found for booking {bookingId}.");
        }

        var refund = Refund.CreateForPostTripComplaint(bookingId, payment.Id, complaintId, amount, requestedAtUtc);
        dbContext.Refunds.Add(refund);
        await dbContext.SaveChangesAsync(cancellationToken);

        var complaint = await dbContext.PostTripComplaints
            .FirstOrDefaultAsync(c => c.Id == complaintId, cancellationToken);
        if (complaint is not null)
        {
            complaint.LinkRefund(refund.Id);
        }
    }
}

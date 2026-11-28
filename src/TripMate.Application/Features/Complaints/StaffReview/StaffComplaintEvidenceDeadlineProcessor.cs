using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.StaffReview;

/// <summary>72-hour evidence expiry resumes Staff review; never decides the complaint or money.</summary>
public sealed class StaffComplaintEvidenceDeadlineProcessor(IApplicationDbContext db, IDateTimeProvider clock)
{
    public async Task<int> ExpireOverdueAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var ids = await db.PostTripComplaints.AsNoTracking()
            .Where(c => c.Status == PostTripComplaintStatus.AwaitingStaffEvidence &&
                db.ComplaintStaffReviewEvents.Any(e => e.ComplaintId == c.Id && e.Action == ComplaintStaffReviewEvent.RequestEvidence && e.DueAtUtc <= now &&
                    !db.ComplaintStaffReviewEvents.Any(later => later.ComplaintId == c.Id && later.Action == ComplaintStaffReviewEvent.RequestEvidence && later.Id > e.Id)))
            .OrderBy(c => c.Id).Select(c => c.Id).Take(100).ToListAsync(ct);
        int count = 0;
        foreach (long id in ids)
        {
            bool changed = await db.ExecuteInSerializableTransactionAsync(async token =>
            {
                var complaint = await db.PostTripComplaints.SingleAsync(c => c.Id == id, token);
                if (complaint.Status != PostTripComplaintStatus.AwaitingStaffEvidence) return false;
                var request = await db.ComplaintStaffReviewEvents.Where(e => e.ComplaintId == id && e.Action == ComplaintStaffReviewEvent.RequestEvidence)
                    .OrderByDescending(e => e.Id).FirstAsync(token);
                if (request.DueAtUtc is null || request.DueAtUtc > now) return false;
                complaint.ReceiveStaffEvidence();
                db.ComplaintStaffReviewEvents.Add(ComplaintStaffReviewEvent.Create(id, null, Guid.NewGuid(),
                    DecideStaffComplaintCommandHandler.Fingerprint(new { RequestId = request.Id, Action = ComplaintStaffReviewEvent.EvidenceExpired }),
                    ComplaintStaffReviewEvent.EvidenceExpired, "The 72-hour evidence deadline elapsed. Automatically returned to Staff review; no decision or refund was changed.",
                    "", null, request.TargetParty, now, request.Id));
                db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(null, "ComplaintStaffEvidenceExpired", ComplaintStaffReviewEvent.AuditEntity,
                    id, now, AuditOutcome.Success));
                await db.SaveChangesAsync(token);
                return true;
            }, ct);
            if (changed) count++;
        }
        return count;
    }
}
using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Complaints.StaffReview;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.PostTripComplaints.Domain;

public sealed class StaffComplaintDeadlineTests
{
    [Fact]
    public async Task EvidenceDeadlineReturnsToStaffOnceWithoutRejectingOrCreatingRefund()
    {
        await using var db = new Context(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var complaint = PostTripComplaint.Create(1, 2, PostTripComplaintReasonCategory.Other, "Not supplied", now, now.AddDays(7), 10000);
        complaint.ProposePartialRefund(3, 2000, "First", now, now.AddDays(3));
        complaint.CounterPartialRefund(2, 8000, "Counter", now, now.AddDays(3));
        complaint.ProposePartialRefund(3, 3000, "Final", now, now.AddDays(3));
        complaint.DeclineFinalPartialRefund(2, "Not accepted");
        complaint.RequestStaffEvidence();
        db.PostTripComplaints.Add(complaint);
        await db.SaveChangesAsync();
        var request = ComplaintStaffReviewEvent.Create(complaint.Id, 4, Guid.NewGuid(), new string('A', 64), ComplaintStaffReviewEvent.RequestEvidence, "Supply receipt", "Section 4", null, "Traveler", now);
        db.ComplaintStaffReviewEvents.Add(request);
        await db.SaveChangesAsync();
        var clock = new FakeDateTimeProvider { UtcNow = now.AddHours(72).AddTicks(-1) };
        var processor = new StaffComplaintEvidenceDeadlineProcessor(db, clock);
        Assert.Equal(0, await processor.ExpireOverdueAsync(default));
        clock.UtcNow = now.AddHours(72);
        Assert.Equal(1, await processor.ExpireOverdueAsync(default));
        Assert.Equal(0, await processor.ExpireOverdueAsync(default));
        Assert.Equal(PostTripComplaintStatus.AwaitingStaffReview, complaint.Status);
        Assert.Null(complaint.RefundId);
        Assert.Single(db.ComplaintStaffReviewEvents.Where(e => e.Action == ComplaintStaffReviewEvent.EvidenceExpired));
    }

    private sealed class Context(DbContextOptions<TestDbContext> options) : TestDbContext(options), IApplicationDbContext
    {
    }
}
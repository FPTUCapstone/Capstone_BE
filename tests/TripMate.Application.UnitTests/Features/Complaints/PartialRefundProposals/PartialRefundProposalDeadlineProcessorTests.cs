using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.Features.Complaints.PartialRefundProposals.PartialRefundProposalTestData;

namespace TripMate.Application.UnitTests.Features.Complaints.PartialRefundProposals;

public sealed class PartialRefundProposalDeadlineProcessorTests
{
    private const string Msg168 =
        "The partial refund proposal for booking BK-UC75 received no response within 3 days and has been closed "
        + "as declined. The complaint will be handled under the partnership contract.";

    [Fact]
    public async Task CloseOverdue_PastDeadline_DeclinesAndNotifiesTravelerAndOperatorWithMsg168()
    {
        await using var db = CreateDbContext();
        var store = new FakePartialRefundProposalStore(db);
        store.Add(Proposal());
        var processor = CreateProcessor(db, store, Deadline.AddMinutes(30));

        int closed = await processor.CloseOverdueAsync(CancellationToken.None);

        closed.Should().Be(1);
        store.Get(ComplaintId).Status.Should().Be(PostTripComplaintStatus.Declined);
        store.Closes.Should().ContainSingle().Which.At.Should().Be(Deadline.AddMinutes(30));
        store.ClosedOutsideTransaction.Should().BeFalse();
        var notifications = await db.Notifications.OrderBy(n => n.UserId).ToListAsync();
        notifications.Select(n => n.UserId).Should().Equal(TravelerId, OperatorId);
        notifications.Should().AllSatisfy(n =>
        {
            n.Type.Should().Be(Notification.PartialRefundProposalExpiredType);
            n.Channel.Should().Be(NotificationChannel.Push);
            n.RelatedEntityType.Should().Be(Notification.PostTripComplaintRelatedEntityType);
            n.RelatedEntityId.Should().Be(ComplaintId);
            n.Body.Should().Be(Msg168);
        });
    }

    [Fact]
    public async Task CloseOverdue_ExactlyAtDeadline_Declines()
    {
        await using var db = CreateDbContext();
        var store = new FakePartialRefundProposalStore(db);
        store.Add(Proposal());

        int closed = await CreateProcessor(db, store, Deadline).CloseOverdueAsync(CancellationToken.None);

        closed.Should().Be(1);
    }

    [Fact]
    public async Task CloseOverdue_BeforeDeadline_LeavesProposalOpen()
    {
        await using var db = CreateDbContext();
        var store = new FakePartialRefundProposalStore(db);
        store.Add(Proposal());

        int closed = await CreateProcessor(db, store, Deadline.AddTicks(-1)).CloseOverdueAsync(CancellationToken.None);

        closed.Should().Be(0);
        store.Get(ComplaintId).Status.Should().Be(PostTripComplaintStatus.AwaitingTravelerResponse);
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task CloseOverdue_WhenTravelerAnsweredConcurrently_SkipsWithoutNotification()
    {
        await using var db = CreateDbContext();
        var store = new FakePartialRefundProposalStore(db);
        store.Add(Proposal());
        store.Add(Proposal(complaintId: ComplaintId + 1));
        store.LoseRaceFor.Add(ComplaintId);

        int closed = await CreateProcessor(db, store, Deadline.AddHours(1)).CloseOverdueAsync(CancellationToken.None);

        closed.Should().Be(1);
        (await db.Notifications.Select(n => n.RelatedEntityId).Distinct().ToListAsync())
            .Should().Equal(ComplaintId + 1);
    }

    [Fact]
    public async Task CloseOverdue_ProcessesAtMostBatchSize()
    {
        await using var db = CreateDbContext();
        var store = new FakePartialRefundProposalStore(db);
        for (long id = 1; id <= 3; id++) store.Add(Proposal(complaintId: id));

        int closed = await CreateProcessor(db, store, Deadline.AddHours(1), batchSize: 2)
            .CloseOverdueAsync(CancellationToken.None);

        closed.Should().Be(2);
    }

    private static PartialRefundProposalDeadlineProcessor CreateProcessor(TestDbContext db,
        FakePartialRefundProposalStore store, DateTimeOffset now, int batchSize = 100) =>
        new(db, store, new FakeDateTimeProvider { UtcNow = now },
            new PartialRefundProposalDeadlineOptions { BatchSize = batchSize });
}
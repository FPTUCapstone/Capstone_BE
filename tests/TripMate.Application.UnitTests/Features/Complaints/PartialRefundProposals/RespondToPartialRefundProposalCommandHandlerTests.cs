using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.Features.Complaints.PartialRefundProposals.PartialRefundProposalTestData;

namespace TripMate.Application.UnitTests.Features.Complaints.PartialRefundProposals;

public sealed class RespondToPartialRefundProposalCommandHandlerTests
{
    private static readonly DateTimeOffset BeforeDeadline = Deadline.AddDays(-2);

    [Fact]
    public async Task Accept_BeforeDeadline_ApprovesWithProposedAmountAndCreatesRefundRequest()
    {
        await using var db = CreateDbContext();
        var (store, refunds, handler) = Arrange(db, BeforeDeadline);

        var result = await handler.Handle(Command(PartialRefundDecision.Accept), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new RespondToPartialRefundProposalResponse(ComplaintId,
            nameof(PostTripComplaintStatus.Approved), ProposedAmount, BeforeDeadline));
        store.Get(ComplaintId).Status.Should().Be(PostTripComplaintStatus.Approved);
        refunds.Created.Should().ContainSingle()
            .Which.Should().Be((ComplaintId, BookingId, ProposedAmount, BeforeDeadline));
        store.ClosedOutsideTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task Accept_NotifiesOperatorWithMsg166()
    {
        await using var db = CreateDbContext();
        var (_, _, handler) = Arrange(db, BeforeDeadline);

        await handler.Handle(Command(PartialRefundDecision.Accept), CancellationToken.None);

        var notification = await db.Notifications.SingleAsync();
        notification.UserId.Should().Be(OperatorId);
        notification.Type.Should().Be(Notification.PartialRefundProposalAcceptedType);
        notification.Channel.Should().Be(NotificationChannel.Push);
        notification.RelatedEntityType.Should().Be(Notification.PostTripComplaintRelatedEntityType);
        notification.RelatedEntityId.Should().Be(ComplaintId);
        notification.Body.Should().Be(
            "The traveler accepted your partial refund proposal of 500,000 VND for booking BK-UC75. "
            + "Please confirm the refund request.");
    }

    [Fact]
    public async Task CounterOffer_BeforeDeadline_RequestsOneFinalOperatorProposal()
    {
        await using var db = CreateDbContext();
        var (store, refunds, handler) = Arrange(db, BeforeDeadline);

        var result = await handler.Handle(new RespondToPartialRefundProposalCommand(ComplaintId,
            PartialRefundDecision.CounterOffer, "The proposal does not cover the missed itinerary items.", 500_000m),
            CancellationToken.None);

        result.Value.Should().Be(new RespondToPartialRefundProposalResponse(ComplaintId,
            nameof(PostTripComplaintStatus.AwaitingOperatorReconsideration), null, BeforeDeadline, 500_000m));
        store.Get(ComplaintId).Status.Should().Be(PostTripComplaintStatus.AwaitingOperatorReconsideration);
        refunds.Created.Should().BeEmpty();
        var notification = await db.Notifications.SingleAsync();
        notification.UserId.Should().Be(OperatorId);
        notification.Type.Should().Be("PartialRefundCounterOffer");
        notification.Body.Should().Be(
            "The traveler requested 500,000 VND for booking BK-UC75. "
            + "Review and submit your final revised proposal within 3 days.");
    }

    [Fact]
    public async Task FinalDecline_AfterRevisedOffer_RoutesToStaffWithoutRefundRequest()
    {
        await using var db = CreateDbContext();
        var revised = Proposal() with { Round = 2, ProposalReason = "Final revised offer." };
        var (store, refunds, handler) = Arrange(db, BeforeDeadline, revised);

        var result = await handler.Handle(new RespondToPartialRefundProposalCommand(ComplaintId,
            PartialRefundDecision.Decline, "The final proposal is still inadequate."), CancellationToken.None);

        result.Value.Status.Should().Be(nameof(PostTripComplaintStatus.AwaitingStaffReview));
        store.Get(ComplaintId).Status.Should().Be(PostTripComplaintStatus.AwaitingStaffReview);
        refunds.Created.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PostTripComplaintStatus.Approved)]
    [InlineData(PostTripComplaintStatus.Declined)]
    [InlineData(PostTripComplaintStatus.AwaitingOperatorResponse)]
    [InlineData(PostTripComplaintStatus.Rejected)]
    [InlineData(PostTripComplaintStatus.Expired)]
    public async Task Respond_WhenNotAwaitingTraveler_ReturnsMsg165AndChangesNothing(PostTripComplaintStatus status)
    {
        // G1: a second answer after the first one was recorded.
        await using var db = CreateDbContext();
        var (store, refunds, handler) = Arrange(db, BeforeDeadline, Proposal(status: status));

        var result = await handler.Handle(Command(PartialRefundDecision.Decline), CancellationToken.None);

        result.ErrorCode.Should().Be(PartialRefundProposalErrorCodes.ResponseClosed);
        store.Closes.Should().BeEmpty();
        refunds.Created.Should().BeEmpty();
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public async Task Respond_AtOrAfterDeadlineBeforeJobRuns_ReturnsMsg165AndChangesNothing(int minutesLate)
    {
        // G5: the status is still AwaitingTravelerResponse because the hourly job has not run yet.
        await using var db = CreateDbContext();
        var (store, refunds, handler) = Arrange(db, Deadline.AddMinutes(minutesLate));

        var result = await handler.Handle(Command(PartialRefundDecision.Accept), CancellationToken.None);

        result.ErrorCode.Should().Be(PartialRefundProposalErrorCodes.ResponseClosed);
        store.Get(ComplaintId).Status.Should().Be(PostTripComplaintStatus.AwaitingTravelerResponse);
        refunds.Created.Should().BeEmpty();
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Accept_WhenConcurrentWriterClosedItFirst_ReturnsMsg165WithoutRefundRequest()
    {
        await using var db = CreateDbContext();
        var (store, refunds, handler) = Arrange(db, BeforeDeadline);
        store.LoseRaceFor.Add(ComplaintId);

        var result = await handler.Handle(Command(PartialRefundDecision.Accept), CancellationToken.None);

        result.ErrorCode.Should().Be(PartialRefundProposalErrorCodes.ResponseClosed);
        refunds.Created.Should().BeEmpty();
        (await db.Notifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Respond_NotOwner_ReturnsMsg126AndChangesNothing()
    {
        await using var db = CreateDbContext();
        var (store, refunds, handler) = Arrange(db, BeforeDeadline, currentUser: SignedInTraveler(OtherTravelerId));

        var result = await handler.Handle(Command(PartialRefundDecision.Accept), CancellationToken.None);

        result.ErrorCode.Should().Be(PartialRefundProposalErrorCodes.Forbidden);
        store.Closes.Should().BeEmpty();
        refunds.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Respond_UnknownComplaint_ReturnsNotFound()
    {
        await using var db = CreateDbContext();
        var store = new FakePartialRefundProposalStore(db);
        var handler = new RespondToPartialRefundProposalCommandHandler(db, store,
            new FakeComplaintRefundRequestCreator(), SignedInTraveler(), new FakeDateTimeProvider { UtcNow = BeforeDeadline });

        var result = await handler.Handle(Command(PartialRefundDecision.Accept), CancellationToken.None);

        result.ErrorCode.Should().Be(PartialRefundProposalErrorCodes.NotFound);
    }

    [Fact]
    public async Task Respond_NotSignedInAsTraveler_ReturnsUnauthorized()
    {
        await using var db = CreateDbContext();
        var (store, _, handler) = Arrange(db, BeforeDeadline,
            currentUser: new FakeCurrentUserService { UserId = OperatorId, Role = nameof(UserRole.TourOperator) });

        var result = await handler.Handle(Command(PartialRefundDecision.Accept), CancellationToken.None);

        result.ErrorCode.Should().Be(PartialRefundProposalErrorCodes.Unauthorized);
        store.Closes.Should().BeEmpty();
    }

    private static RespondToPartialRefundProposalCommand Command(PartialRefundDecision decision) =>
        new(ComplaintId, decision);

    private static (FakePartialRefundProposalStore Store, FakeComplaintRefundRequestCreator Refunds,
        RespondToPartialRefundProposalCommandHandler Handler) Arrange(TestDbContext db, DateTimeOffset now,
            PartialRefundProposal? proposal = null, FakeCurrentUserService? currentUser = null)
    {
        var store = new FakePartialRefundProposalStore(db);
        store.Add(proposal ?? Proposal());
        var refunds = new FakeComplaintRefundRequestCreator();
        var handler = new RespondToPartialRefundProposalCommandHandler(db, store, refunds,
            currentUser ?? SignedInTraveler(), new FakeDateTimeProvider { UtcNow = now });
        return (store, refunds, handler);
    }
}

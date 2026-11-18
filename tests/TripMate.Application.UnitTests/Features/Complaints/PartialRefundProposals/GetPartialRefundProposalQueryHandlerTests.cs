using FluentAssertions;

using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.Features.Complaints.PartialRefundProposals.PartialRefundProposalTestData;

namespace TripMate.Application.UnitTests.Features.Complaints.PartialRefundProposals;

public sealed class GetPartialRefundProposalQueryHandlerTests
{
    [Fact]
    public async Task Handle_OwnerBeforeDeadline_ReturnsAmountDeadlineAndCanRespond()
    {
        var store = new FakePartialRefundProposalStore();
        store.Add(Proposal());
        var handler = CreateHandler(store, SignedInTraveler(), Deadline.AddHours(-1));

        var result = await handler.Handle(new GetPartialRefundProposalQuery(ComplaintId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new PartialRefundProposalResponse(ComplaintId, BookingId, BookingCode,
            ProposedAmount, Deadline, nameof(PostTripComplaintStatus.AwaitingTravelerResponse), CanRespond: true,
            ProposalReason: "Initial proposal rationale", Round: 1, CanCounterOffer: true,
            CanDeclineFinally: false, RefundableAmount: ProposedAmount));
    }

    [Fact]
    public async Task Handle_AfterDeadlineBeforeJobRuns_CannotRespond()
    {
        var store = new FakePartialRefundProposalStore();
        store.Add(Proposal());
        var handler = CreateHandler(store, SignedInTraveler(), Deadline.AddMinutes(15));

        var result = await handler.Handle(new GetPartialRefundProposalQuery(ComplaintId), CancellationToken.None);

        result.Value.CanRespond.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_AlreadyAnswered_ReturnsFinalStatusAndCannotRespond()
    {
        var store = new FakePartialRefundProposalStore();
        store.Add(Proposal(status: PostTripComplaintStatus.Approved));
        var handler = CreateHandler(store, SignedInTraveler(), Deadline.AddHours(-1));

        var result = await handler.Handle(new GetPartialRefundProposalQuery(ComplaintId), CancellationToken.None);

        result.Value.Status.Should().Be(nameof(PostTripComplaintStatus.Approved));
        result.Value.CanRespond.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_NotOwner_ReturnsForbiddenMsg126()
    {
        var store = new FakePartialRefundProposalStore();
        store.Add(Proposal());
        var handler = CreateHandler(store, SignedInTraveler(OtherTravelerId), Deadline.AddHours(-1));

        var result = await handler.Handle(new GetPartialRefundProposalQuery(ComplaintId), CancellationToken.None);

        result.ErrorCode.Should().Be(PartialRefundProposalErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_UnknownComplaint_ReturnsNotFound()
    {
        var handler = CreateHandler(new FakePartialRefundProposalStore(), SignedInTraveler(), Deadline);

        var result = await handler.Handle(new GetPartialRefundProposalQuery(ComplaintId), CancellationToken.None);

        result.ErrorCode.Should().Be(PartialRefundProposalErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(null, nameof(UserRole.Traveler))]
    [InlineData(TravelerId, nameof(UserRole.TourOperator))]
    [InlineData(TravelerId, null)]
    public async Task Handle_NotSignedInAsTraveler_ReturnsUnauthorized(long? userId, string? role)
    {
        var store = new FakePartialRefundProposalStore();
        store.Add(Proposal());
        var handler = CreateHandler(store, new FakeCurrentUserService { UserId = userId, Role = role }, Deadline);

        var result = await handler.Handle(new GetPartialRefundProposalQuery(ComplaintId), CancellationToken.None);

        result.ErrorCode.Should().Be(PartialRefundProposalErrorCodes.Unauthorized);
    }

    private static GetPartialRefundProposalQueryHandler CreateHandler(FakePartialRefundProposalStore store,
        FakeCurrentUserService currentUser, DateTimeOffset now) =>
        new(store, currentUser, new FakeDateTimeProvider { UtcNow = now });
}
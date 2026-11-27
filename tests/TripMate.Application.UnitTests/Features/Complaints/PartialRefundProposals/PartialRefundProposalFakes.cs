using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Complaints.PartialRefundProposals;

internal static class PartialRefundProposalTestData
{
    public const long TravelerId = 11;
    public const long OtherTravelerId = 12;
    public const long OperatorId = 22;
    public const long ComplaintId = 501;
    public const long BookingId = 301;
    public const string BookingCode = "BK-UC75";
    public const decimal ProposedAmount = 500_000m;

    // The proposal was made on Friday 06/11 10:00 (UTC+7), so the deadline is Monday 09/11 10:00 (UTC+7).
    public static readonly DateTimeOffset ProposedAt = new(2026, 11, 6, 3, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Deadline = PartialRefundProposalRules.DeadlineFrom(ProposedAt);

    public static PartialRefundProposal Proposal(
        long complaintId = ComplaintId,
        PostTripComplaintStatus status = PostTripComplaintStatus.AwaitingTravelerResponse,
        DateTimeOffset? deadline = null) =>
        new(complaintId, BookingId, BookingCode, TravelerId, OperatorId, status, ProposedAmount,
            deadline ?? Deadline, "Initial proposal rationale", 1, null, ProposedAmount);

    public static TestDbContext CreateDbContext() => new(new DbContextOptionsBuilder<TestDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    public static FakeCurrentUserService SignedInTraveler(long userId = TravelerId) =>
        new() { UserId = userId, Role = nameof(UserRole.Traveler) };
}

internal sealed class FakePartialRefundProposalStore(TestDbContext? dbContext = null) : IPartialRefundProposalStore
{
    private readonly Dictionary<long, PartialRefundProposal> _proposals = [];

    public HashSet<long> LoseRaceFor { get; } = [];

    public List<(long ComplaintId, PostTripComplaintStatus Outcome, DateTimeOffset At)> Closes { get; } = [];

    public bool ClosedOutsideTransaction { get; private set; }

    public void Add(PartialRefundProposal proposal) => _proposals[proposal.ComplaintId] = proposal;

    public PartialRefundProposal Get(long complaintId) => _proposals[complaintId];

    public Task<PartialRefundProposal?> FindAsync(long complaintId, CancellationToken cancellationToken) =>
        Task.FromResult(_proposals.GetValueOrDefault(complaintId));

    public Task<bool> TryCloseAsync(long complaintId, PostTripComplaintStatus outcome, DateTimeOffset respondedAtUtc,
        CancellationToken cancellationToken)
    {
        if (dbContext is not null && !dbContext.IsExecutingSerializableTransaction) ClosedOutsideTransaction = true;
        if (LoseRaceFor.Contains(complaintId)
            || !_proposals.TryGetValue(complaintId, out var proposal)
            || proposal.Status != PostTripComplaintStatus.AwaitingTravelerResponse)
        {
            return Task.FromResult(false);
        }

        _proposals[complaintId] = proposal with { Status = outcome };
        Closes.Add((complaintId, outcome, respondedAtUtc));
        return Task.FromResult(true);
    }

    public Task<bool> TryCounterAsync(long complaintId, long travelerUserId, decimal requestedAmount, string reason,
        DateTimeOffset counteredAtUtc, DateTimeOffset operatorReconsiderationDeadlineUtc,
        CancellationToken cancellationToken)
    {
        if (LoseRaceFor.Contains(complaintId) || !_proposals.TryGetValue(complaintId, out var proposal) ||
            proposal.Status != PostTripComplaintStatus.AwaitingTravelerResponse || proposal.Round != 1)
            return Task.FromResult(false);
        _proposals[complaintId] = proposal with
        {
            Status = PostTripComplaintStatus.AwaitingOperatorReconsideration,
            OperatorReconsiderationDeadlineUtc = operatorReconsiderationDeadlineUtc
        };
        return Task.FromResult(true);
    }

    public Task<bool> TryDeclineFinallyAsync(long complaintId, long travelerUserId, string reason,
        DateTimeOffset declinedAtUtc, CancellationToken cancellationToken)
    {
        if (LoseRaceFor.Contains(complaintId) || !_proposals.TryGetValue(complaintId, out var proposal) ||
            proposal.Status != PostTripComplaintStatus.AwaitingTravelerResponse || proposal.Round != 2)
            return Task.FromResult(false);
        _proposals[complaintId] = proposal with { Status = PostTripComplaintStatus.AwaitingStaffReview };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PartialRefundProposal>> ListOverdueAsync(DateTimeOffset nowUtc, int maxCount,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PartialRefundProposal>>(_proposals.Values
            .Where(p => p.Status == PostTripComplaintStatus.AwaitingTravelerResponse && p.ResponseDeadlineUtc <= nowUtc)
            .OrderBy(p => p.ResponseDeadlineUtc)
            .Take(maxCount)
            .ToList());
}

internal sealed class FakeComplaintRefundRequestCreator : IComplaintRefundRequestCreator
{
    public List<(long ComplaintId, long BookingId, decimal Amount, DateTimeOffset At)> Created { get; } = [];

    public Task CreateForAcceptedComplaintAsync(long complaintId, long bookingId, decimal amount,
        DateTimeOffset requestedAtUtc, CancellationToken cancellationToken)
    {
        Created.Add((complaintId, bookingId, amount, requestedAtUtc));
        return Task.CompletedTask;
    }
}

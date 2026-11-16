using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.PartialRefundProposals;

/// <summary>UC-75 Normal Flow step 2: show the proposed amount and the response deadline.</summary>
public sealed record GetPartialRefundProposalQuery(long ComplaintId) : IRequest<Result<PartialRefundProposalResponse>>;

public sealed record PartialRefundProposalResponse(
    long ComplaintId,
    long BookingId,
    string BookingCode,
    decimal ProposedAmount,
    DateTimeOffset ResponseDeadlineUtc,
    string Status,
    bool CanRespond);

public sealed class GetPartialRefundProposalQueryHandler(
    IPartialRefundProposalStore store,
    ICurrentUserService currentUser,
    IDateTimeProvider clock) : IRequestHandler<GetPartialRefundProposalQuery, Result<PartialRefundProposalResponse>>
{
    public async Task<Result<PartialRefundProposalResponse>> Handle(GetPartialRefundProposalQuery request,
        CancellationToken cancellationToken)
    {
        if (!PartialRefundProposalAccess.TryGetTraveler(currentUser, out long travelerId))
            return PartialRefundProposalAccess.Unauthorized<PartialRefundProposalResponse>();

        var proposal = await store.FindAsync(request.ComplaintId, cancellationToken);
        if (proposal is null) return PartialRefundProposalAccess.NotFound<PartialRefundProposalResponse>();
        if (proposal.TravelerUserId != travelerId)
            return PartialRefundProposalAccess.Forbidden<PartialRefundProposalResponse>();

        return Result.Success(new PartialRefundProposalResponse(proposal.ComplaintId, proposal.BookingId,
            proposal.BookingCode, proposal.ProposedAmount, proposal.ResponseDeadlineUtc, proposal.Status.ToString(),
            PartialRefundProposalRules.IsOpen(proposal.Status, proposal.ResponseDeadlineUtc, clock.UtcNow)));
    }
}

internal static class PartialRefundProposalAccess
{
    public static bool TryGetTraveler(ICurrentUserService currentUser, out long travelerId)
    {
        travelerId = currentUser.UserId ?? 0;
        return travelerId > 0 && currentUser.Role == nameof(UserRole.Traveler);
    }

    public static Result<T> Unauthorized<T>() => Result.Failure<T>(PartialRefundProposalErrorCodes.Unauthorized,
        "An authenticated Traveler account is required.");

    public static Result<T> NotFound<T>() => Result.Failure<T>(PartialRefundProposalErrorCodes.NotFound,
        "The complaint could not be found.");

    public static Result<T> Forbidden<T>() => Result.Failure<T>(PartialRefundProposalErrorCodes.Forbidden,
        PartialRefundProposalErrorCodes.Msg126);

    public static Result<T> ResponseClosed<T>() => Result.Failure<T>(PartialRefundProposalErrorCodes.ResponseClosed,
        PartialRefundProposalErrorCodes.Msg165);
}
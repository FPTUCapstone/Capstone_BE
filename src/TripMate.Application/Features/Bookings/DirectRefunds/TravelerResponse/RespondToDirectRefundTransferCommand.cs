using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.DirectRefunds.TravelerResponse;

public sealed record RespondToDirectRefundTransferCommand(
    long CaseId,
    long TransferId,
    string ResponseType,
    long? ReceivedAmountVnd,
    string? Note,
    byte[] ExpectedVersion) : IRequest<Result>;

public sealed class RespondToDirectRefundTransferCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<RespondToDirectRefundTransferCommand, Result>
{
    private const string ReceivedInFull = "ReceivedInFull";
    private const string NotReceived = "NotReceived";
    private const string PartiallyReceived = "PartiallyReceived";

    public async Task<Result> Handle(
        RespondToDirectRefundTransferCommand request, CancellationToken cancellationToken)
    {
        if (request.ResponseType is not (ReceivedInFull or NotReceived or PartiallyReceived))
            return Result.Failure(DirectRefundErrorCodes.InvalidState,
                $"ResponseType must be one of: {ReceivedInFull}, {NotReceived}, {PartiallyReceived}.");

        if (request.ResponseType == PartiallyReceived && (request.ReceivedAmountVnd ?? 0) <= 0)
            return Result.Failure(DirectRefundErrorCodes.AmountInvalid,
                "ReceivedAmountVnd is required and must be positive for PartiallyReceived.");

        DateTimeOffset nowUtc = clock.UtcNow;

        return await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var (directCase, travelerUserId, _) =
                await DirectRefundOwnershipResolver.ResolveAsync(dbContext, request.CaseId, ct);

            if (directCase is null)
                return Result.Failure(DirectRefundErrorCodes.NotFound, "Direct refund case not found.");

            if (currentUser.UserId != travelerUserId)
                return Result.Failure(DirectRefundErrorCodes.AccessDenied, "Access denied.");

            if (directCase.Status != DirectRefundCase.StatusAwaitingTravelerConfirmation)
                return Result.Failure(DirectRefundErrorCodes.InvalidState,
                    "Response can only be submitted when status is AwaitingTravelerConfirmation.");

            // Verify the transfer belongs to this case
            bool transferExists = await dbContext.DirectRefundTransfers.AsNoTracking()
                .AnyAsync(t => t.Id == request.TransferId && t.CaseId == request.CaseId, ct);
            if (!transferExists)
                return Result.Failure(DirectRefundErrorCodes.NotFound, "Transfer not found for this case.");

            // Load tracked case for concurrency update
            var tracked = await dbContext.DirectRefundCases
                .Where(c => c.Id == request.CaseId)
                .SingleAsync(ct);

            if (!tracked.RowVersion.SequenceEqual(request.ExpectedVersion))
                return Result.Failure(DirectRefundErrorCodes.VersionConflict, "The case was modified concurrently.");

            if (request.ResponseType == ReceivedInFull)
            {
                tracked.Complete(DirectRefundCase.MethodTravelerConfirmed, "1.0", nowUtc);
            }
            else
            {
                // NotReceived or PartiallyReceived → dispute
                tracked.Dispute(nowUtc);
            }

            await dbContext.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}
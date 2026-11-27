using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Application.Features.Traveler.RefundAccount;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Recipient;

public sealed record ConfirmDirectRefundRecipientCommand(
    long CaseId,
    string BankCode,
    string AccountNumber,
    string AccountHolder,
    byte[] ExpectedVersion) : IRequest<Result>;

public sealed class ConfirmDirectRefundRecipientCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IPiiProtector piiProtector)
    : IRequestHandler<ConfirmDirectRefundRecipientCommand, Result>
{
    public async Task<Result> Handle(
        ConfirmDirectRefundRecipientCommand request, CancellationToken cancellationToken)
    {
        if (!TravelerRefundAccountStore.IsValid(request.BankCode, request.AccountNumber, request.AccountHolder))
            return Result.Failure(DirectRefundErrorCodes.ValidationFailed, "A valid recipient within the supported field lengths is required.");
        if (string.IsNullOrWhiteSpace(request.BankCode))
            return Result.Failure(DirectRefundErrorCodes.RecipientRequired, "Bank code is required.");
        if (string.IsNullOrWhiteSpace(request.AccountNumber))
            return Result.Failure(DirectRefundErrorCodes.RecipientRequired, "Account number is required.");
        if (string.IsNullOrWhiteSpace(request.AccountHolder))
            return Result.Failure(DirectRefundErrorCodes.RecipientRequired, "Account holder name is required.");

        return await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var (directCase, travelerUserId, _) =
                await DirectRefundOwnershipResolver.ResolveAsync(dbContext, request.CaseId, ct);

            if (directCase is null)
                return Result.Failure(DirectRefundErrorCodes.NotFound, "Direct refund case not found.");

            if (currentUser.UserId != travelerUserId)
                return Result.Failure(DirectRefundErrorCodes.AccessDenied, "Access denied.");

            if (directCase.Status != DirectRefundCase.StatusAwaitingRecipient)
                return Result.Failure(DirectRefundErrorCodes.InvalidState,
                    $"Recipient can only be confirmed when status is {DirectRefundCase.StatusAwaitingRecipient}.");

            // Re-load with tracking for optimistic concurrency update
            var tracked = await dbContext.DirectRefundCases
                .Where(c => c.Id == request.CaseId)
                .SingleAsync(ct);

            if (!tracked.RowVersion.SequenceEqual(request.ExpectedVersion))
                return Result.Failure(DirectRefundErrorCodes.VersionConflict, "The case was modified concurrently.");

            tracked.ConfirmRecipient(request.BankCode.Trim(), request.AccountNumber.Trim(), request.AccountHolder.Trim(), clock.UtcNow);
            await TravelerRefundAccountStore.SaveAsync(dbContext, piiProtector, travelerUserId!.Value,
                request.BankCode, request.AccountNumber, request.AccountHolder, clock.UtcNow, ct);

            await dbContext.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}
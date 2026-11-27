using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Transfer;

public sealed record ReportDirectRefundTransferCommand(
    long CaseId,
    long ReportedAmountVnd,
    string BankReference,
    DateTimeOffset ActualTransferredAtUtc,
    IReadOnlyList<long> EvidenceIds,
    byte[] ExpectedVersion) : IRequest<Result<ReportDirectRefundTransferResponse>>;

public sealed record ReportDirectRefundTransferResponse(
    long TransferId,
    DateTimeOffset ConfirmationDeadlineAtUtc);

public sealed class ReportDirectRefundTransferCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<ReportDirectRefundTransferCommand, Result<ReportDirectRefundTransferResponse>>
{
    public async Task<Result<ReportDirectRefundTransferResponse>> Handle(
        ReportDirectRefundTransferCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BankReference))
            return Result.Failure<ReportDirectRefundTransferResponse>(
                DirectRefundErrorCodes.EvidenceInvalid, "Bank reference is required.");

        if (request.ReportedAmountVnd <= 0)
            return Result.Failure<ReportDirectRefundTransferResponse>(
                DirectRefundErrorCodes.AmountInvalid, "Reported amount must be positive.");

        DateTimeOffset nowUtc = clock.UtcNow;
        if (request.ActualTransferredAtUtc > nowUtc)
            return Result.Failure<ReportDirectRefundTransferResponse>(
                DirectRefundErrorCodes.TransferFutureDate, "Actual transfer date cannot be in the future.");

        if (request.EvidenceIds.Count < DirectRefundEvidence.MinFilesPerReport)
            return Result.Failure<ReportDirectRefundTransferResponse>(
                DirectRefundErrorCodes.MissingEvidence,
                $"At least {DirectRefundEvidence.MinFilesPerReport} evidence file is required.");

        return await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var (directCase, travelerUserId, operatorUserId) =
                await DirectRefundOwnershipResolver.ResolveAsync(dbContext, request.CaseId, ct);

            if (directCase is null)
                return Result.Failure<ReportDirectRefundTransferResponse>(
                    DirectRefundErrorCodes.NotFound, "Direct refund case not found.");

            if (currentUser.UserId != operatorUserId)
                return Result.Failure<ReportDirectRefundTransferResponse>(
                    DirectRefundErrorCodes.AccessDenied, "Access denied.");

            if (directCase.Status != DirectRefundCase.StatusAwaitingTransfer)
                return Result.Failure<ReportDirectRefundTransferResponse>(
                    DirectRefundErrorCodes.InvalidState,
                    "Transfer can only be reported when status is AwaitingTransfer.");

            // Verify all evidence IDs belong to this case
            if (request.EvidenceIds.Count > 0)
            {
                int ownedCount = await dbContext.DirectRefundEvidences.AsNoTracking()
                    .CountAsync(e => e.CaseId == request.CaseId
                                     && request.EvidenceIds.Contains(e.Id), ct);
                if (ownedCount != request.EvidenceIds.Count)
                    return Result.Failure<ReportDirectRefundTransferResponse>(
                        DirectRefundErrorCodes.EvidenceNotOwnedByCase,
                        "One or more evidence files do not belong to this case.");
            }

            // Load tracked case for concurrency update
            var tracked = await dbContext.DirectRefundCases
                .Where(c => c.Id == request.CaseId)
                .SingleAsync(ct);

            if (!tracked.RowVersion.SequenceEqual(request.ExpectedVersion))
                return Result.Failure<ReportDirectRefundTransferResponse>(
                    DirectRefundErrorCodes.VersionConflict, "The case was modified concurrently.");

            var transfer = DirectRefundTransfer.Create(
                request.CaseId,
                request.ReportedAmountVnd,
                request.BankReference,
                request.ActualTransferredAtUtc,
                nowUtc,
                tracked.RecipientBankCode!,
                tracked.RecipientAccountNumber!,
                tracked.RecipientAccountHolder!);

            dbContext.DirectRefundTransfers.Add(transfer);
            tracked.MarkTransferReported(nowUtc);

            // Notify Traveler that transfer has been reported
            if (travelerUserId.HasValue)
            {
                dbContext.Notifications.Add(
                    DirectRefundNotifications.TransferReported(
                        travelerUserId.Value, request.CaseId,
                        transfer.ConfirmationDeadlineAtUtc, nowUtc));
            }

            await dbContext.SaveChangesAsync(ct);

            return Result.Success(new ReportDirectRefundTransferResponse(
                transfer.Id, transfer.ConfirmationDeadlineAtUtc));
        }, cancellationToken);
    }
}
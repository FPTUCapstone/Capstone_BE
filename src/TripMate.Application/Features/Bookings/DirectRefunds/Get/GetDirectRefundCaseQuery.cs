using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Get;

public sealed record GetDirectRefundCaseQuery(long CaseId) : IRequest<Result<DirectRefundCaseResponse>>;

public sealed record DirectRefundEvidenceSummary(
    long EvidenceId,
    string FileName,
    string ContentType,
    DateTimeOffset UploadedAtUtc);

public sealed record DirectRefundTransferSummary(
    long TransferId,
    long ReportedAmountVnd,
    string BankReference,
    DateTimeOffset ActualTransferredAtUtc,
    DateTimeOffset ReportedAtUtc,
    DateTimeOffset ConfirmationDeadlineAtUtc,
    IReadOnlyList<DirectRefundEvidenceSummary> Evidence);

public sealed record DirectRefundCaseResponse(
    long CaseId,
    long BookingId,
    string BookingCode,
    string SourceType,
    long RefundAmountVnd,
    string Status,
    string? RecipientBankCode,
    string? RecipientAccountNumberMasked,
    string? RecipientAccountHolder,
    DateTimeOffset? RecipientConfirmedAtUtc,
    DateTimeOffset? TransferDeadlineAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? CompletionMethod,
    bool IsOverdueFlag,
    DateTimeOffset CreatedAtUtc,
    DirectRefundTransferSummary? PendingTransfer,
    IReadOnlyList<string> AllowedActions,
    string RowVersion,
    string? DisputeResolution,
    string? DisputeResolutionReason,
    DateTimeOffset? DisputeResolvedAtUtc);

public sealed class GetDirectRefundCaseQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<GetDirectRefundCaseQuery, Result<DirectRefundCaseResponse>>
{
    public async Task<Result<DirectRefundCaseResponse>> Handle(
        GetDirectRefundCaseQuery request, CancellationToken cancellationToken)
    {
        var row = await dbContext.DirectRefundCases.AsNoTracking()
            .Where(c => c.Id == request.CaseId)
            .Join(dbContext.Bookings.AsNoTracking(), c => c.BookingId, b => b.Id,
                (c, b) => new { Case = c, b.TravelerUserId, b.TourScheduleId, b.BookingCode })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
            return Result.Failure<DirectRefundCaseResponse>(
                DirectRefundErrorCodes.NotFound, "Direct refund case not found.");

        long? operatorUserId = await dbContext.TourSchedules.AsNoTracking()
            .Where(s => s.Id == row.TourScheduleId)
            .Join(dbContext.Tours.AsNoTracking(), s => s.TourId, t => t.Id,
                (s, t) => (long?)t.OperatorUserId)
            .SingleOrDefaultAsync(cancellationToken);

        long? callerId = currentUser.UserId;
        string? callerRole = currentUser.Role;

        bool isTraveler = callerRole == "Traveler" && callerId == row.TravelerUserId;
        bool isOperator = callerRole == "TourOperator" && callerId == operatorUserId;
        bool isStaffOrAdmin = callerRole is "Staff" or "Administrator";

        if (!isTraveler && !isOperator && !isStaffOrAdmin)
            return Result.Failure<DirectRefundCaseResponse>(
                DirectRefundErrorCodes.AccessDenied, "Access denied.");

        var directCase = row.Case;

        string? maskedAccount = directCase.RecipientAccountNumber is null
            ? null
            : MaskAccountNumber(directCase.RecipientAccountNumber);

        // Preserve the immutable latest report for correction prefill and completed-case history.
        var pendingTransfer = await BuildPendingTransferAsync(request.CaseId, cancellationToken);

        bool isAdmin = callerRole == "Administrator";
        var allowedActions = BuildAllowedActions(directCase, isTraveler, isOperator, isAdmin);

        return Result.Success(new DirectRefundCaseResponse(
            directCase.Id,
            directCase.BookingId,
            row.BookingCode,
            directCase.SourceType,
            directCase.RefundAmountVnd,
            directCase.Status,
            directCase.RecipientBankCode,
            maskedAccount,
            directCase.RecipientAccountHolder,
            directCase.RecipientConfirmedAtUtc,
            directCase.TransferDeadlineAtUtc,
            directCase.CompletedAtUtc,
            directCase.CompletionMethod,
            directCase.IsOverdueFlag,
            directCase.CreatedAtUtc,
            pendingTransfer,
            allowedActions,
            Convert.ToBase64String(directCase.RowVersion),
            directCase.DisputeResolution,
            directCase.DisputeResolutionReason,
            directCase.DisputeResolvedAtUtc));
    }

    private async Task<DirectRefundTransferSummary?> BuildPendingTransferAsync(
        long caseId, CancellationToken cancellationToken)
    {
        var transfer = await dbContext.DirectRefundTransfers.AsNoTracking()
            .Where(t => t.CaseId == caseId)
            .OrderByDescending(t => t.ReportedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (transfer is null) return null;

        var evidences = await dbContext.DirectRefundEvidences.AsNoTracking()
            .Where(e => e.CaseId == caseId)
            .OrderBy(e => e.UploadedAt)
            .Select(e => new DirectRefundEvidenceSummary(e.Id, e.OriginalFileName, e.ContentType, e.UploadedAt))
            .ToListAsync(cancellationToken);

        return new DirectRefundTransferSummary(
            transfer.Id,
            transfer.ReportedAmountVnd,
            transfer.BankReference,
            transfer.ActualTransferredAtUtc,
            transfer.ReportedAtUtc,
            transfer.ConfirmationDeadlineAtUtc,
            evidences);
    }

    private static string MaskAccountNumber(string accountNumber)
    {
        if (accountNumber.Length <= 4) return new string('*', accountNumber.Length);
        return new string('*', accountNumber.Length - 4) + accountNumber[^4..];
    }

    private static IReadOnlyList<string> BuildAllowedActions(
        DirectRefundCase directCase, bool isTraveler, bool isOperator, bool isAdmin)
    {
        var actions = new List<string>();

        switch (directCase.Status)
        {
            case DirectRefundCase.StatusAwaitingRecipient:
                if (isTraveler) actions.Add("ConfirmRecipient");
                break;
            case DirectRefundCase.StatusAwaitingTransfer:
                if (isOperator) actions.Add("UploadEvidence");
                if (isOperator) actions.Add("ReportTransfer");
                break;
            case DirectRefundCase.StatusAwaitingTravelerConfirmation:
                if (isTraveler) actions.Add("RespondToTransfer");
                break;
            case DirectRefundCase.StatusDisputed:
                if (isAdmin) actions.Add("ResolveDispute");
                break;
        }

        return actions.AsReadOnly();
    }
}
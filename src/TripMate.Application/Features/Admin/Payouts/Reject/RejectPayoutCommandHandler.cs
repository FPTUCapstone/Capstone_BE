using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Payouts.Reject;

public sealed class RejectPayoutCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<RejectPayoutCommand, Result<RejectPayoutResponseDto>>
{
    public async Task<Result<RejectPayoutResponseDto>> Handle(
        RejectPayoutCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != "Staff")
        {
            return Result.Failure<RejectPayoutResponseDto>(
                PayoutErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        var reviewerId = currentUserService.UserId.Value;

        // D2: 10-500 characters after trim. Validated before touching the row so an invalid
        // reason never reaches the entity and never shows as a 404/409 instead of a 400.
        var trimmedReason = request.Reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length < Payout.RejectionReasonMinLength
            || trimmedReason.Length > Payout.RejectionReasonMaxLength)
        {
            var reasonMessage = $"Rejection reason must be between {Payout.RejectionReasonMinLength} " +
                $"and {Payout.RejectionReasonMaxLength} characters.";

            // Both an errorCode and an errors.reason entry, so the Web can map either to MSG157
            // (decision D6) without a FluentValidation detour — HandleFailure copies ErrorMetadata
            // straight into the ProblemDetails extensions.
            return Result.Failure<RejectPayoutResponseDto>(
                PayoutErrorCodes.RejectionReasonInvalid,
                reasonMessage,
                new Dictionary<string, object?>
                {
                    ["errors"] = new Dictionary<string, string[]> { ["reason"] = [reasonMessage] },
                });
        }

        // Tracked (not AsNoTracking): this is a command that updates the entity in the same
        // SaveChangesAsync (AGENTS.md §3.2).
        var payout = await dbContext.Payouts
            .FirstOrDefaultAsync(candidate => candidate.Id == request.PayoutId, cancellationToken);

        if (payout is null)
        {
            return Result.Failure<RejectPayoutResponseDto>(
                PayoutErrorCodes.NotFound,
                PayoutErrorCodes.NotFoundMessage);
        }

        var now = dateTimeProvider.UtcNow;

        if (!payout.Reject(reviewerId, trimmedReason, now))
        {
            return Result.Failure<RejectPayoutResponseDto>(
                PayoutErrorCodes.NotPendingReview,
                PayoutErrorCodes.NotPendingReviewMessage,
                new Dictionary<string, object?> { ["currentStatus"] = payout.Status });
        }

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            reviewerId,
            AuditActionTypes.PayoutRejected,
            AuditEntityTypes.Payout,
            payout.Id,
            now,
            AuditOutcome.Success,
            reason: trimmedReason));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // I02: another request (approve or reject) committed the same PendingReview row
            // first. Re-read the committed status with a fresh, untracked query rather than
            // trusting this process's in-memory entity.
            var currentStatus = await dbContext.Payouts
                .AsNoTracking()
                .Where(candidate => candidate.Id == request.PayoutId)
                .Select(candidate => candidate.Status)
                .FirstOrDefaultAsync(cancellationToken);

            return Result.Failure<RejectPayoutResponseDto>(
                PayoutErrorCodes.NotPendingReview,
                PayoutErrorCodes.NotPendingReviewMessage,
                new Dictionary<string, object?> { ["currentStatus"] = currentStatus });
        }

        return Result.Success(new RejectPayoutResponseDto(
            payout.Id.ToString(CultureInfo.InvariantCulture),
            payout.Status,
            now,
            trimmedReason));
    }
}
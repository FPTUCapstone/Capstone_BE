using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Payouts.Approve;

public sealed class ApprovePayoutCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<ApprovePayoutCommand, Result<ApprovePayoutResponseDto>>
{
    public async Task<Result<ApprovePayoutResponseDto>> Handle(
        ApprovePayoutCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != "Staff")
        {
            return Result.Failure<ApprovePayoutResponseDto>(
                PayoutErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        var reviewerId = currentUserService.UserId.Value;

        // Tracked (not AsNoTracking): this is a command that updates the entity in the same
        // SaveChangesAsync (AGENTS.md §3.2).
        var payout = await dbContext.Payouts
            .FirstOrDefaultAsync(candidate => candidate.Id == request.PayoutId, cancellationToken);

        if (payout is null)
        {
            return Result.Failure<ApprovePayoutResponseDto>(
                PayoutErrorCodes.NotFound,
                PayoutErrorCodes.NotFoundMessage);
        }

        var now = dateTimeProvider.UtcNow;

        // Pre-check: the entity's own invariant guard. A concurrent writer that commits first is
        // caught below by the Status concurrency token instead (I02), not here.
        if (!payout.Approve(reviewerId, now))
        {
            return Result.Failure<ApprovePayoutResponseDto>(
                PayoutErrorCodes.NotPendingReview,
                PayoutErrorCodes.NotPendingReviewMessage,
                new Dictionary<string, object?> { ["currentStatus"] = payout.Status });
        }

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            reviewerId,
            AuditActionTypes.PayoutApproved,
            AuditEntityTypes.Payout,
            payout.Id,
            now,
            AuditOutcome.Success));

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

            return Result.Failure<ApprovePayoutResponseDto>(
                PayoutErrorCodes.NotPendingReview,
                PayoutErrorCodes.NotPendingReviewMessage,
                new Dictionary<string, object?> { ["currentStatus"] = currentStatus });
        }

        return Result.Success(new ApprovePayoutResponseDto(
            payout.Id.ToString(CultureInfo.InvariantCulture),
            payout.Status,
            now));
    }
}

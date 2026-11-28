using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Dispute;

public sealed record ResolveDirectRefundDisputeCommand(
    long CaseId,
    string Resolution,
    string Reason,
    byte[] ExpectedVersion) : IRequest<Result>;

public sealed class ResolveDirectRefundDisputeCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<ResolveDirectRefundDisputeCommand, Result>
{
    public const string ResolutionCompleted = "Completed";
    public const string ResolutionCorrectionRequired = "CorrectionRequired";
    public const string ResolutionMoreEvidenceRequired = "MoreEvidenceRequired";

    public async Task<Result> Handle(
        ResolveDirectRefundDisputeCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.Role is not ("Administrator" or "Staff"))
            return Result.Failure(DirectRefundErrorCodes.Unauthorized, "Only administrators may resolve disputes.");

        if (request.Resolution is not (ResolutionCompleted or ResolutionCorrectionRequired or ResolutionMoreEvidenceRequired))
            return Result.Failure(DirectRefundErrorCodes.InvalidState,
                $"Resolution must be one of: {ResolutionCompleted}, {ResolutionCorrectionRequired}, {ResolutionMoreEvidenceRequired}.");

        if (string.IsNullOrWhiteSpace(request.Reason))
            return Result.Failure(DirectRefundErrorCodes.InvalidState, "Reason is required for dispute resolution.");

        DateTimeOffset nowUtc = clock.UtcNow;

        return await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var directCase = await dbContext.DirectRefundCases
                .Where(c => c.Id == request.CaseId)
                .SingleOrDefaultAsync(ct);

            if (directCase is null)
                return Result.Failure(DirectRefundErrorCodes.NotFound, "Direct refund case not found.");

            if (directCase.Status != DirectRefundCase.StatusDisputed)
                return Result.Failure(DirectRefundErrorCodes.InvalidState,
                    "Only disputed cases can be resolved.");

            if (!directCase.RowVersion.SequenceEqual(request.ExpectedVersion))
                return Result.Failure(DirectRefundErrorCodes.VersionConflict, "The case was modified concurrently.");

            if (request.Resolution == ResolutionCompleted)
                directCase.ResolveDisputeCompleted(request.Reason, nowUtc);
            else
                directCase.ResolveDisputeReopen(request.Resolution, request.Reason, nowUtc);

            await dbContext.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}
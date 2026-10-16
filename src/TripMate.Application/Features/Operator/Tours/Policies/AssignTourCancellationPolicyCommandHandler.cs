using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Policies;

public sealed class AssignTourCancellationPolicyCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<AssignTourCancellationPolicyCommand, Result<AssignTourCancellationPolicyResponse>>
{
    public async Task<Result<AssignTourCancellationPolicyResponse>> Handle(
        AssignTourCancellationPolicyCommand request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return AccessDenied();
        long operatorId = currentUser.UserId.Value;
        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            bool eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
                profile.UserId == operatorId && profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
                profile.User.Role == UserRole.TourOperator && profile.User.Status == AccountStatus.Active,
                transactionCancellationToken);
            if (!eligible) return AccessDenied();
            Tour? tour = await dbContext.Tours.Include(candidate => candidate.CancellationPolicyRules)
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == request.TourId && candidate.OperatorUserId == operatorId,
                    transactionCancellationToken);
            if (tour is null) return Result.Failure<AssignTourCancellationPolicyResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
            if (tour.Status is not (TourStatus.Draft or TourStatus.Rejected)) return Result.Failure<AssignTourCancellationPolicyResponse>(OperatorTourErrorCodes.EditLocked, "Only Draft or Rejected tours can be edited.");
            CancellationPolicyTemplate? template = await dbContext.CancellationPolicyTemplates
                .Include(policy => policy.Rules)
                .SingleOrDefaultAsync(policy => policy.Id == request.CancellationPolicyTemplateId && policy.IsActive,
                    transactionCancellationToken);
            if (template is null || template.Rules.Count == 0)
                return Result.Failure<AssignTourCancellationPolicyResponse>(OperatorTourErrorCodes.CancellationPolicyUnavailable,
                    "The selected cancellation policy is unavailable.");

            // Delete-then-insert in two saves so the (tour, cutoff) unique index never sees both rows.
            dbContext.TourCancellationPolicyRules.RemoveRange(tour.CancellationPolicyRules.ToList());
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            tour.ApplyCancellationPolicySnapshot(template, clock.UtcNow);
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return Result.Success(new AssignTourCancellationPolicyResponse(tour.Id, template.Id));
        }, cancellationToken);
    }

    private static Result<AssignTourCancellationPolicyResponse> AccessDenied() =>
        Result.Failure<AssignTourCancellationPolicyResponse>(OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}
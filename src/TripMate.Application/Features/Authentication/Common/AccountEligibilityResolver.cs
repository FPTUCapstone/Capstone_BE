using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Authentication.Common;

/// <summary>
/// Resolves effective authentication eligibility without normalizing stored account state.
/// Application approval remains a separate requirement for Partner business authorization.
/// </summary>
public static class AccountEligibilityResolver
{
    public static async Task<Result<CurrentAccountContext>> ResolveAsync(
        IApplicationDbContext dbContext, User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (user.Status)
        {
            case AccountStatus.PendingEmailVerification:
                return Result.Failure<CurrentAccountContext>(AuthErrorCodes.AccountPendingVerification, "Email has not been verified.");
            case AccountStatus.Locked:
                return Result.Failure<CurrentAccountContext>(AuthErrorCodes.AccountLocked, "Account is locked.");
            case AccountStatus.Inactive:
                return Result.Failure<CurrentAccountContext>(AuthErrorCodes.AccountInactive, "Account is inactive.");
            case AccountStatus.Active:
                return Result.Success(new CurrentAccountContext(AccountStatus.Active,
                    await ReadApplicationStatusAsync(dbContext, user, cancellationToken)));
            case AccountStatus.PendingApproval:
            case AccountStatus.Rejected:
                break;
            default:
                return Unresolved();
        }

        // Only the persisted marker is evidence; provider claims and response fallbacks
        // must not repair or substitute for missing legacy verification data.
        if (user.Role != UserRole.TourOperator
            || user.EmailVerifiedAtUtc is not { } verifiedAt
            || verifiedAt < user.CreatedAtUtc || verifiedAt > now)
        {
            return Unresolved();
        }

        var profile = await dbContext.OperatorProfiles.AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == user.Id, cancellationToken);
        var matches = profile is not null && (
            (user.Status == AccountStatus.PendingApproval && profile.ApprovalStatus == OperatorApprovalStatus.PendingApproval)
            || (user.Status == AccountStatus.Rejected && profile.ApprovalStatus == OperatorApprovalStatus.Rejected));

        return matches ? Result.Success(new CurrentAccountContext(AccountStatus.Active, profile!.ApprovalStatus.ToString())) : Unresolved();
    }

    private static async Task<string?> ReadApplicationStatusAsync(
        IApplicationDbContext dbContext, User user, CancellationToken cancellationToken)
    {
        if (user.Role != UserRole.TourOperator) return null;
        var approval = await dbContext.OperatorProfiles.AsNoTracking()
            .Where(p => p.UserId == user.Id).Select(p => (OperatorApprovalStatus?)p.ApprovalStatus)
            .SingleOrDefaultAsync(cancellationToken);
        return approval is OperatorApprovalStatus.Approved or OperatorApprovalStatus.PendingApproval or OperatorApprovalStatus.Rejected
            ? approval.Value.ToString() : null;
    }

    private static Result<CurrentAccountContext> Unresolved() => Result.Failure<CurrentAccountContext>(
        AuthErrorCodes.AccountStateUnresolved, "Account state could not be resolved.");
}
public sealed record CurrentAccountContext(AccountStatus Status, string? ApplicationStatus);
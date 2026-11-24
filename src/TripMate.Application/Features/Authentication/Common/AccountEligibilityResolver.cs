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
        IApplicationDbContext dbContext, User user, DateTimeOffset now, CancellationToken cancellationToken,
        bool contractGateEnabled = false)
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
                return await ResolveActiveAsync(dbContext, user, cancellationToken, contractGateEnabled);
            case AccountStatus.PendingContractSignature:
                if (!contractGateEnabled) return Unresolved();
                break;
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
            || (user.Status == AccountStatus.Rejected && profile.ApprovalStatus == OperatorApprovalStatus.Rejected)
            || (user.Status == AccountStatus.PendingContractSignature && profile.ApprovalStatus == OperatorApprovalStatus.Approved));

        if (!matches) return Unresolved();
        if (user.Status == AccountStatus.PendingContractSignature)
        {
            var contract = await dbContext.OperatorContracts.AsNoTracking()
                .Where(item => item.OperatorUserId == user.Id && item.Status == OperatorContractStatus.PendingSignature)
                .OrderByDescending(item => item.Id).Select(item => new { item.Id, item.Status }).FirstOrDefaultAsync(cancellationToken);
            return contract is null ? Unresolved() : Result.Success(new CurrentAccountContext(AccountStatus.Active,
                profile!.ApprovalStatus.ToString(), "AwaitingSignature", contract.Status.ToString(), contract.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return Result.Success(new CurrentAccountContext(AccountStatus.Active, profile!.ApprovalStatus.ToString(),
            user.Status == AccountStatus.Rejected ? "Rejected" : "AwaitingReview"));
    }

    private static async Task<Result<CurrentAccountContext>> ResolveActiveAsync(
        IApplicationDbContext dbContext, User user, CancellationToken cancellationToken, bool contractGateEnabled)
    {
        var approval = await ReadApplicationStatusAsync(dbContext, user, cancellationToken);
        if (user.Role != UserRole.TourOperator) return Result.Success(new CurrentAccountContext(AccountStatus.Active, approval));
        var onboarding = approval switch { "Approved" => "Active", "PendingApproval" => "AwaitingReview", "Rejected" => "Rejected", _ => "Unresolved" };
        if (!contractGateEnabled || approval != nameof(OperatorApprovalStatus.Approved))
            return Result.Success(new CurrentAccountContext(AccountStatus.Active, approval, onboarding));
        var contract = await dbContext.OperatorContracts.AsNoTracking()
            .Where(item => item.OperatorUserId == user.Id && item.Status == OperatorContractStatus.Signed)
            .OrderByDescending(item => item.Id).Select(item => new { item.Id, item.Status }).FirstOrDefaultAsync(cancellationToken);
        if (contract is not null) return Result.Success(new CurrentAccountContext(AccountStatus.Active, approval, "Active",
            contract.Status.ToString(), contract.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return await dbContext.OperatorContractLegacyExemptions.AsNoTracking().AnyAsync(item => item.OperatorUserId == user.Id, cancellationToken)
            ? Result.Success(new CurrentAccountContext(AccountStatus.Active, approval, "Active")) : Unresolved();
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
public sealed record CurrentAccountContext(AccountStatus Status, string? ApplicationStatus,
    string? OnboardingState = null, string? ContractStatus = null, string? CurrentContractId = null);
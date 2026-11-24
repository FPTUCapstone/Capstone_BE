using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Contracts.Common;
using TripMate.Domain.Enums;

namespace TripMate.Api.Authorization;

public static class OperatorContractWorkspacePolicies
{
    public const string Workspace = "OperatorContracts.Workspace";
}

public sealed class OperatorContractWorkspaceRequirement : IAuthorizationRequirement;

/// <summary>Requires persisted operator eligibility even when the caller holds a previously issued JWT.</summary>
public sealed class OperatorContractWorkspaceAuthorizationHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IOptions<OperatorContractOptions> options)
    : AuthorizationHandler<OperatorContractWorkspaceRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OperatorContractWorkspaceRequirement requirement)
    {
        // Shared-role endpoints retain their existing authorization requirements.
        if (!options.Value.Enabled || currentUser.Role != nameof(UserRole.TourOperator))
        {
            context.Succeed(requirement);
            return;
        }
        if (currentUser.UserId is not > 0) return;
        var ct = context.Resource is HttpContext httpContext ? httpContext.RequestAborted : CancellationToken.None;
        var operatorId = currentUser.UserId.Value;
        var isEligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
            profile.UserId == operatorId && profile.User.Role == UserRole.TourOperator &&
            profile.User.Status == AccountStatus.Active && profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
            (dbContext.OperatorContracts.Any(contract => contract.OperatorUserId == operatorId && contract.Status == OperatorContractStatus.Signed)
             || dbContext.OperatorContractLegacyExemptions.Any(exemption => exemption.OperatorUserId == operatorId)), ct);
        if (isEligible) context.Succeed(requirement);
    }
}
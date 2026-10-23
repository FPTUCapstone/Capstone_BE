using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Policies;

public sealed class ListCancellationPolicyTemplatesQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<ListCancellationPolicyTemplatesQuery, Result<IReadOnlyList<CancellationPolicyTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<CancellationPolicyTemplateDto>>> Handle(
        ListCancellationPolicyTemplatesQuery request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue || !await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
                profile.UserId == currentUser.UserId.Value &&
                profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
                profile.User.Role == UserRole.TourOperator &&
                profile.User.Status == AccountStatus.Active,
                cancellationToken))
        {
            return Result.Failure<IReadOnlyList<CancellationPolicyTemplateDto>>(
                OperatorTourErrorCodes.AccessRequired,
                "An active approved Tour Operator account is required.");
        }

        var policies = await dbContext.CancellationPolicyTemplates.AsNoTracking()
            .Where(policy => policy.IsActive)
            .OrderBy(policy => policy.Name)
            .Select(policy => new CancellationPolicyTemplateDto(
                policy.Id,
                policy.Name,
                policy.Description,
                policy.Rules.OrderByDescending(rule => rule.DaysBeforeDeparture)
                    .Select(rule => new CancellationPolicyRuleDto(
                        rule.DaysBeforeDeparture, rule.CancellationFeePercent))
                    .ToList()))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<CancellationPolicyTemplateDto>>(policies);
    }
}
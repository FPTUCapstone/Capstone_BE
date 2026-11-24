using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Contracts;

public sealed class OperatorContractIssuer(IApplicationDbContext db, ICurrentUserService actor)
{
    public async Task<Result<OperatorContract>> CreateForApprovalAsync(User owner, OperatorProfile profile, DateTimeOffset now, CancellationToken ct)
    {
        if (actor.Role != nameof(UserRole.Staff) || actor.UserId is not > 0)
            return Result.Failure<OperatorContract>("contract.forbidden", "Only Staff can issue a contract after reviewing the application.");
        if (owner.Status != AccountStatus.PendingApproval || owner.Role != UserRole.TourOperator ||
            owner.EmailVerifiedAtUtc is not { } verified || verified < owner.CreatedAtUtc || verified > now || string.IsNullOrWhiteSpace(owner.Email))
            return Result.Failure<OperatorContract>("contract.validation", "The operator must have a verified email and a pending application.");
        var head = await db.ContractTemplatePublicationHeads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        var template = head?.CurrentPublishedTemplateId is long templateId
            ? await db.OperatorContractTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == templateId && x.Status == ContractTemplateStatus.Published, ct) : null;
        if (template is null || template.Version is null || !template.ValidationValid || template.RenderState != ContractTemplateRenderState.Ready)
            return Result.Failure<OperatorContract>("contract.template_unavailable", "Publish and verify a contract template before approving a new operator.");
        if (await db.OperatorContracts.AnyAsync(x => x.OperatorUserId == owner.Id && x.Status != OperatorContractStatus.Voided, ct))
            return Result.Failure<OperatorContract>("contract.conflict", "A contract has already been issued for this application.");
        return Result.Success(OperatorContract.Create(owner.Id, template.Id, template.Version.Value, template.SourceStorageKey,
            template.SourceSha256, template.Title, profile.CompanyName, profile.TaxCode, profile.BusinessLicenseNo,
            profile.ContactAddress ?? string.Empty, owner.Email, actor.UserId.Value, now));
    }
}
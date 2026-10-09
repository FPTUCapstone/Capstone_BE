using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Application.Common;
using TripMate.Domain.Common;

namespace TripMate.Application.Features.Operator.Application.GetCurrent;

public sealed class GetOperatorApplicationQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IOperatorDocumentStorage storage,
    IDateTimeProvider clock)
    : IRequestHandler<GetOperatorApplicationQuery, Result<OperatorApplicationDto>>
{
    public async Task<Result<OperatorApplicationDto>> Handle(
        GetOperatorApplicationQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long userId || currentUser.Role != "TourOperator")
        {
            return Result.Failure<OperatorApplicationDto>(
                OperatorApplicationErrorCodes.Forbidden,
                "Tour Operator access is required.");
        }

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        var profile = await db.OperatorProfiles.AsNoTracking()
            .Include(candidate => candidate.Documents)
            .FirstOrDefaultAsync(candidate => candidate.UserId == userId, cancellationToken);

        if (user is null || profile is null)
        {
            return Result.Failure<OperatorApplicationDto>(
                OperatorApplicationErrorCodes.NotFound,
                "Tour Operator application not found.");
        }

        var resubmissionCount = await db.AuditLogs.AsNoTracking().CountAsync(
            audit => audit.ActionType == AuditActionTypes.OperatorApplicationResubmit &&
                audit.AffectedEntity == AuditEntityTypes.OperatorProfile &&
                audit.AffectedEntityId == userId,
            cancellationToken);

        var expiresAtUtc = clock.UtcNow.AddMinutes(5);
        var documents = profile.Documents
            .OrderByDescending(document => document.UploadedAtUtc)
            .ThenByDescending(document => document.Id)
            .Select(document =>
            {
                var url = storage.CreateTemporaryDownloadUrl(document.FileUrl, expiresAtUtc);
                return new OperatorApplicationDocumentDto(
                    document.Id,
                    document.DocumentType.ToString(),
                    document.Status.ToString(),
                    document.UploadedAtUtc,
                    url?.AbsoluteUri,
                    url is null ? null : expiresAtUtc);
            })
            .ToArray();

        return Result.Success(new OperatorApplicationDto(
            user.Id,
            user.Status.ToString(),
            profile.ApprovalStatus.ToString(),
            profile.CompanyName,
            profile.BusinessLicenseNo,
            profile.TaxCode,
            profile.ContactAddress,
            user.FullName,
            profile.ContactPhone,
            profile.RejectionReason,
            profile.ReviewedAtUtc,
            resubmissionCount,
            documents));
    }
}


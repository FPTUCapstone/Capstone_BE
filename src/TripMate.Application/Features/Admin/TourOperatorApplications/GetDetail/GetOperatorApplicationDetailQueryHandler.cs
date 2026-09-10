using MediatR;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.GetDetail;

public class GetOperatorApplicationDetailQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetOperatorApplicationDetailQuery, Result<TourOperatorApplicationDetailDto>>
{
    public async Task<Result<TourOperatorApplicationDetailDto>> Handle(
        GetOperatorApplicationDetailQuery request,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        if (user is null)
        {
            return Result.Failure<TourOperatorApplicationDetailDto>(
                TourOperatorApplicationErrorCodes.NotFound,
                "Tour Operator application not found.");
        }

        if (user.Role != UserRole.TourOperator)
        {
            return Result.Failure<TourOperatorApplicationDetailDto>(
                TourOperatorApplicationErrorCodes.WrongRole,
                "Target account is not a Tour Operator.");
        }

        var profile = await dbContext.OperatorProfiles
            .AsNoTracking()
            .Include(p => p.Documents)
            .FirstOrDefaultAsync(p => p.UserId == request.UserId, cancellationToken);

        if (profile is null)
        {
            return Result.Failure<TourOperatorApplicationDetailDto>(
                TourOperatorApplicationErrorCodes.NotFound,
                "Operator profile not found.");
        }

        var documentsDto = profile.Documents
            .Select(d => new OperatorDocumentDto(
                d.Id,
                d.DocumentType,
                d.FileUrl,
                d.Status,
                d.UploadedAtUtc))
            .ToList();

        var detailDto = new TourOperatorApplicationDetailDto(
            user.Id,
            user.Role,
            user.Status,
            profile.ApprovalStatus,
            profile.CompanyName,
            profile.TaxCode,
            profile.BusinessLicenseNo,
            profile.ContactPhone,
            profile.ContactAddress,
            documentsDto,
            profile.ReviewedBy,
            profile.ReviewedAtUtc,
            profile.RejectionReason);

        return Result.Success(detailDto);
    }
}

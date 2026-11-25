using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.GetList;

public sealed record GetOperatorApplicationListQuery(int Page = 1, string? Status = null, string? Search = null)
    : IRequest<Result<OperatorApplicationListDto>>;

public sealed record OperatorApplicationListDto(IReadOnlyList<OperatorApplicationSummaryDto> Items, int TotalCount);
public sealed record OperatorApplicationSummaryDto(long UserId, string CompanyName, string TaxCode,
    string? ContactPhone, DateTimeOffset SubmittedAt, string ApplicationStatus, int DocumentsCount, bool HasBusinessLicense);

public sealed class GetOperatorApplicationListQueryHandler(IApplicationDbContext db)
    : IRequestHandler<GetOperatorApplicationListQuery, Result<OperatorApplicationListDto>>
{
    public async Task<Result<OperatorApplicationListDto>> Handle(GetOperatorApplicationListQuery request, CancellationToken cancellationToken)
    {
        if (request.Page < 1 || request.Page > 100000 || request.Search?.Length > 200 ||
            request.Status is not (null or "" or "All" or "Pending" or "Approved" or "Rejected"))
            return Result.Failure<OperatorApplicationListDto>("auth.request_invalid", "Invalid application list filters.");

        var query = db.OperatorProfiles.AsNoTracking().Where(p => p.User.Role == UserRole.TourOperator);
        if (request.Status is "Pending" or "Approved" or "Rejected")
        {
            var status = request.Status == "Pending" ? OperatorApprovalStatus.PendingApproval
                : request.Status == "Approved" ? OperatorApprovalStatus.Approved : OperatorApprovalStatus.Rejected;
            query = query.Where(p => p.ApprovalStatus == status);
        }
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            var isId = long.TryParse(search, out var id);
            query = query.Where(p => p.CompanyName.Contains(search) || p.TaxCode.Contains(search) || (isId && p.UserId == id));
        }
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(p => p.CreatedAtUtc).ThenByDescending(p => p.UserId)
            .Skip((request.Page - 1) * 50).Take(50)
            .Select(p => new OperatorApplicationSummaryDto(p.UserId, p.CompanyName, p.TaxCode, p.ContactPhone,
                p.CreatedAtUtc, p.ApprovalStatus == OperatorApprovalStatus.PendingApproval ? "Pending"
                    : p.ApprovalStatus == OperatorApprovalStatus.Approved ? "Approved" : "Rejected",
                p.Documents.Count, p.Documents.Any(d => d.DocumentType == OperatorDocumentType.BusinessLicense)))
            .ToListAsync(cancellationToken);
        return Result.Success(new OperatorApplicationListDto(items, count));
    }
}
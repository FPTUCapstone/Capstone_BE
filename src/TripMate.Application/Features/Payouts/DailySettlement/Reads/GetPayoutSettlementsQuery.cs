using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement.Reads;

/// <summary>UC-72: Staff/Administrator read-only list of settlements, filterable by status and
/// run-date range. Read-only — PC-01/PC-02 of UC-72 (no record is modified).</summary>
public sealed record GetPayoutSettlementsQuery(
    string? Status = null,
    DateOnly? RunDateFrom = null,
    DateOnly? RunDateTo = null,
    int PageNumber = 1,
    int PageSize = GetPayoutSettlementsQuery.DefaultPageSize)
    : IRequest<Result<PayoutSettlementsResponseDto>>
{
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;
}

public sealed record PayoutSettlementListItemDto(
    long SettlementId,
    long BookingId,
    long TourOperatorUserId,
    DateOnly RunDate,
    string Kind,
    decimal PayoutAmount,
    string Status,
    string? TransferReference);

public sealed record PayoutSettlementsResponseDto(
    int PageNumber, int PageSize, int TotalCount, IReadOnlyList<PayoutSettlementListItemDto> Items);

public sealed class GetPayoutSettlementsQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<GetPayoutSettlementsQuery, Result<PayoutSettlementsResponseDto>>
{
    public async Task<Result<PayoutSettlementsResponseDto>> Handle(GetPayoutSettlementsQuery request, CancellationToken cancellationToken)
    {
        if (currentUserService.Role != nameof(UserRole.Staff) && currentUserService.Role != nameof(UserRole.Administrator))
            return Result.Failure<PayoutSettlementsResponseDto>(DailyPayoutErrorCodes.Forbidden, DailyPayoutErrorCodes.ForbiddenMessage);

        var pageSize = Math.Clamp(request.PageSize, 1, GetPayoutSettlementsQuery.MaximumPageSize);
        var pageNumber = Math.Max(request.PageNumber, 1);

        var query = dbContext.PayoutSettlements.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Status))
            query = query.Where(s => s.Status == request.Status);
        if (request.RunDateFrom is { } from)
            query = query.Where(s => s.RunDate >= from);
        if (request.RunDateTo is { } to)
            query = query.Where(s => s.RunDate <= to);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(s => s.RunDate).ThenBy(s => s.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new PayoutSettlementListItemDto(
                s.Id, s.BookingId, s.TourOperatorUserId, s.RunDate, s.Kind, s.PayoutAmount, s.Status, s.TransferReference))
            .ToListAsync(cancellationToken);

        return Result.Success(new PayoutSettlementsResponseDto(pageNumber, pageSize, totalCount, items));
    }
}

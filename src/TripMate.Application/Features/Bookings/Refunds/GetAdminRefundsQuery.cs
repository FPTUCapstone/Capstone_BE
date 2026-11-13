using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>UC-42 read endpoint for FE (Q-FE2, spec v1.2 §3.3): the same list serves Staff's
/// "Refund Return Reconciliation" screen (filtered to ReturnReported) and the Administrator's
/// "Escalated Refunds Monitor" (filtered to Escalated/Reconciliation) — one or more statuses.</summary>
public sealed record GetAdminRefundsQuery(IReadOnlyList<string>? Status = null, int PageNumber = 1, int PageSize = 20)
    : IRequest<Result<AdminRefundsResponseDto>>;

public sealed record AdminRefundListItemDto(long RefundId, long BookingId, string BookingCode, decimal Amount,
    string Status, DateTimeOffset? ReturnDeadlineAtUtc, DateTimeOffset RequestedAtUtc);

public sealed record AdminRefundsResponseDto(
    int PageNumber, int PageSize, int TotalCount, IReadOnlyList<AdminRefundListItemDto> Items);

public sealed class GetAdminRefundsQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetAdminRefundsQuery, Result<AdminRefundsResponseDto>>
{
    public async Task<Result<AdminRefundsResponseDto>> Handle(GetAdminRefundsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.Role != nameof(UserRole.Staff) && currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<AdminRefundsResponseDto>(BookingCancellationErrorCodes.Unauthorized,
                "Staff or Administrator access is required to list refunds.");

        int pageSize = Math.Clamp(request.PageSize, 1, 100);
        int pageNumber = Math.Max(request.PageNumber, 1);

        var query = dbContext.Refunds.AsNoTracking();
        if (request.Status is { Count: > 0 } statuses)
        {
            List<RefundStatus> parsedStatuses = statuses
                .Select(status => Enum.TryParse<RefundStatus>(status, out RefundStatus parsed) ? parsed : (RefundStatus?)null)
                .Where(parsed => parsed is not null)
                .Select(parsed => parsed!.Value)
                .ToList();
            if (parsedStatuses.Count == 0)
                return Result.Success(new AdminRefundsResponseDto(pageNumber, pageSize, 0, []));
            query = query.Where(refund => parsedStatuses.Contains(refund.Status));
        }

        int totalCount = await query.CountAsync(cancellationToken);
        List<AdminRefundListItemDto> items = await query
            .OrderByDescending(refund => refund.RequestedAtUtc).ThenBy(refund => refund.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(refund => new AdminRefundListItemDto(refund.Id, refund.BookingId, refund.Booking.BookingCode,
                refund.Amount, refund.Status.ToString(), refund.ReturnDeadlineAtUtc, refund.RequestedAtUtc))
            .ToListAsync(cancellationToken);

        return Result.Success(new AdminRefundsResponseDto(pageNumber, pageSize, totalCount, items));
    }
}
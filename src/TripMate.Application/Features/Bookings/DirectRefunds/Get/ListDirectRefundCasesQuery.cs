using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Get;

public sealed record ListDirectRefundCasesQuery(string? Status = null, int PageNumber = 1, int PageSize = 20)
    : IRequest<Result<DirectRefundCaseListResponse>>
{
    public const int DefaultPageNumber = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public static readonly string[] AllowedStatuses =
    [
        DirectRefundCase.StatusAwaitingRecipient, DirectRefundCase.StatusAwaitingTransfer,
        DirectRefundCase.StatusAwaitingTravelerConfirmation, DirectRefundCase.StatusDisputed,
        DirectRefundCase.StatusCompleted,
    ];
}

public sealed record DirectRefundCaseListItem(long CaseId, string BookingCode, long RefundAmountVnd,
    string Status, DateTimeOffset CreatedAtUtc);

public sealed record DirectRefundCaseListResponse(IReadOnlyList<DirectRefundCaseListItem> Items,
    int TotalCount, int PageNumber, int PageSize);

public sealed class ListDirectRefundCasesQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<ListDirectRefundCasesQuery, Result<DirectRefundCaseListResponse>>
{
    public async Task<Result<DirectRefundCaseListResponse>> Handle(ListDirectRefundCasesQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long userId || userId <= 0
            || currentUser.Role is not (nameof(UserRole.Administrator) or nameof(UserRole.TourOperator)))
            return Result.Failure<DirectRefundCaseListResponse>(DirectRefundErrorCodes.AccessDenied,
                "Administrator or Tour Operator access is required.");

        var offset = ((long)request.PageNumber - 1) * request.PageSize;
        if (request.PageNumber < ListDirectRefundCasesQuery.DefaultPageNumber || request.PageSize < 1
            || request.PageSize > ListDirectRefundCasesQuery.MaxPageSize || offset > int.MaxValue
            || request.Status is not null && !ListDirectRefundCasesQuery.AllowedStatuses.Contains(request.Status, StringComparer.Ordinal))
            return Result.Failure<DirectRefundCaseListResponse>(DirectRefundErrorCodes.ValidationFailed,
                "Use a valid refund status and pagination values.");

        var cases = dbContext.DirectRefundCases.AsNoTracking();
        if (currentUser.Role == nameof(UserRole.Administrator))
            cases = cases.Where(item => item.Status == DirectRefundCase.StatusDisputed || item.DisputeResolution != null);
        else
        {
            // Ownership is derived solely from persisted booking/schedule/tour relationships.
            cases = from item in cases
                    join booking in dbContext.Bookings.AsNoTracking() on item.BookingId equals booking.Id
                    join schedule in dbContext.TourSchedules.AsNoTracking() on booking.TourScheduleId equals schedule.Id
                    join tour in dbContext.Tours.AsNoTracking() on schedule.TourId equals tour.Id
                    where tour.OperatorUserId == userId
                    select item;
        }

        if (request.Status is not null) cases = cases.Where(item => item.Status == request.Status);
        var rows = from item in cases
                   join booking in dbContext.Bookings.AsNoTracking() on item.BookingId equals booking.Id
                   select new { Case = item, booking.BookingCode };
        var total = await rows.CountAsync(cancellationToken);
        var items = await rows.OrderByDescending(item => item.Case.CreatedAtUtc).ThenByDescending(item => item.Case.Id)
            .Skip((int)offset).Take(request.PageSize)
            .Select(item => new DirectRefundCaseListItem(item.Case.Id, item.BookingCode,
                item.Case.RefundAmountVnd, item.Case.Status, item.Case.CreatedAtUtc)).ToListAsync(cancellationToken);
        return Result.Success(new DirectRefundCaseListResponse(items, total, request.PageNumber, request.PageSize));
    }
}
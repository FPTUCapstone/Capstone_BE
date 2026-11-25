using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Payouts.DailySettlement;
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
    long? SettlementId,
    long? BatchId,
    long BookingId,
    long TourOperatorUserId,
    DateOnly? RunDate,
    string Kind,
    decimal PayoutAmount,
    string Status,
    string? TransferReference);

public sealed record PayoutSettlementsResponseDto(
    int PageNumber, int PageSize, int TotalCount, IReadOnlyList<PayoutSettlementListItemDto> Items);

/// <summary>UC-72 (SRS v12 §3.9.9.1): a status this query synthesizes for display — never a real
/// <see cref="TripMate.Domain.Entities.PayoutSettlementStatus"/> persisted on any batch or
/// settlement, since no such row exists yet for a booking still Awaiting its Window.</summary>
public static class PayoutSettlementDisplayStatus
{
    public const string AwaitingWindow = "Awaiting Window";
}

public sealed class GetPayoutSettlementsQueryHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUserService,
    DailyPayoutEligibilityReader eligibilityReader, IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GetPayoutSettlementsQuery, Result<PayoutSettlementsResponseDto>>
{
    public async Task<Result<PayoutSettlementsResponseDto>> Handle(GetPayoutSettlementsQuery request, CancellationToken cancellationToken)
    {
        if (currentUserService.Role != nameof(UserRole.Staff) && currentUserService.Role != nameof(UserRole.Administrator))
            return Result.Failure<PayoutSettlementsResponseDto>(DailyPayoutErrorCodes.Forbidden, DailyPayoutErrorCodes.ForbiddenMessage);

        var pageSize = Math.Clamp(request.PageSize, 1, GetPayoutSettlementsQuery.MaximumPageSize);
        var pageNumber = Math.Max(request.PageNumber, 1);

        var statusFilter = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status;
        var hasDateFilter = request.RunDateFrom is not null || request.RunDateTo is not null;

        var combined = new List<PayoutSettlementListItemDto>();

        // Awaiting Window rows have no RunDate, so a date-range filter can never match one —
        // and asking for a status other than Awaiting Window excludes them anyway.
        if (!hasDateFilter && (statusFilter is null || statusFilter == PayoutSettlementDisplayStatus.AwaitingWindow))
        {
            var awaitingBookings = await eligibilityReader.LoadAwaitingWindowBookingsAsync(dateTimeProvider.UtcNow, cancellationToken);
            combined.AddRange(awaitingBookings.Select(b => new PayoutSettlementListItemDto(
                SettlementId: null, BatchId: null, b.BookingId, b.TourOperatorUserId, RunDate: null, b.Kind,
                PayoutAmount: DailyPayoutAmountCalculator.CalculateAdvanceAmount(b.PaidAmount),
                PayoutSettlementDisplayStatus.AwaitingWindow, TransferReference: null)));
        }

        if (statusFilter != PayoutSettlementDisplayStatus.AwaitingWindow)
        {
            var query = dbContext.PayoutSettlements.AsNoTracking().AsQueryable();
            if (statusFilter is not null)
                query = query.Where(s => s.Status == statusFilter);
            if (request.RunDateFrom is { } from)
                query = query.Where(s => s.RunDate >= from);
            if (request.RunDateTo is { } to)
                query = query.Where(s => s.RunDate <= to);

            var settlements = await query
                .OrderByDescending(s => s.RunDate).ThenBy(s => s.Id)
                .Select(s => new PayoutSettlementListItemDto(
                    s.Id, s.BatchId, s.BookingId, s.TourOperatorUserId, s.RunDate, s.Kind, s.PayoutAmount, s.Status, s.TransferReference))
                .ToListAsync(cancellationToken);
            combined.AddRange(settlements);
        }

        var totalCount = combined.Count;
        var items = combined.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();

        return Result.Success(new PayoutSettlementsResponseDto(pageNumber, pageSize, totalCount, items));
    }
}
using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Admin.Payouts.GetSettlementList;

public sealed class GetSettlementPayoutsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<GetSettlementPayoutsQuery, Result<PayoutsResponseDto>>
{
    public async Task<Result<PayoutsResponseDto>> Handle(
        GetSettlementPayoutsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<PayoutsResponseDto>(
                PayoutErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        if (request.PeriodFrom.HasValue && request.PeriodTo.HasValue
            && request.PeriodFrom.Value > request.PeriodTo.Value)
        {
            return Result.Failure<PayoutsResponseDto>(
                PayoutErrorCodes.InvalidPeriodRange,
                PayoutErrorCodes.InvalidPeriodRangeMessage);
        }

        var filtered = dbContext.Payouts
            .AsNoTracking()
            .Where(payout => payout.Status == Payout.StatusApprovedForSettlement);

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var pattern = ContainsPattern(request.Keyword.Trim());
            filtered = filtered.Where(payout =>
                EF.Functions.Like(Payout.PayoutCodePrefix + payout.Id.ToString(), pattern, "\\") ||
                EF.Functions.Like(payout.Operator.CompanyName, pattern, "\\"));
        }

        if (request.PeriodFrom.HasValue)
        {
            filtered = filtered.Where(payout => payout.PeriodEnd >= request.PeriodFrom.Value);
        }

        if (request.PeriodTo.HasValue)
        {
            filtered = filtered.Where(payout => payout.PeriodStart <= request.PeriodTo.Value);
        }

        var summary = await filtered
            .GroupBy(_ => 1)
            .Select(group => new SummaryRow(group.Count(), group.Sum(payout => payout.NetAmount)))
            .FirstOrDefaultAsync(cancellationToken) ?? new SummaryRow(0, 0m);

        var skipLong = ((long)request.PageNumber - 1) * request.PageSize;
        var pageRows = skipLong > int.MaxValue
            ? []
            : await filtered
                .OrderByDescending(payout => payout.PeriodStart)
                .ThenByDescending(payout => payout.OperatorUserId)
                .ThenByDescending(payout => payout.Id)
                .Skip((int)skipLong)
                .Take(request.PageSize)
                .Select(payout => new PageRow(
                    payout.Id,
                    payout.OperatorUserId,
                    payout.Operator.CompanyName,
                    payout.PeriodStart,
                    payout.PeriodEnd,
                    payout.GrossRevenue,
                    payout.CommissionAmount,
                    payout.NetAmount,
                    payout.RequestedAtUtc,
                    payout.Status))
                .ToListAsync(cancellationToken);

        var totalPages = request.PageSize > 0
            ? (int)Math.Ceiling(summary.TotalCount / (double)request.PageSize)
            : 0;

        return Result.Success(new PayoutsResponseDto(
            request.PageNumber,
            request.PageSize,
            summary.TotalCount,
            totalPages,
            summary.TotalAmount,
            pageRows.Select(row => new PayoutListItemDto(
                row.PayoutId.ToString(CultureInfo.InvariantCulture),
                Payout.PayoutCodePrefix + row.PayoutId.ToString(CultureInfo.InvariantCulture),
                new PayoutOperatorDto(
                    row.OperatorUserId.ToString(CultureInfo.InvariantCulture),
                    row.CompanyName),
                row.PeriodStart,
                row.PeriodEnd,
                row.GrossRevenue,
                row.CommissionAmount,
                row.NetAmount,
                row.RequestedAtUtc,
                row.Status)).ToList()));
    }

    private static string ContainsPattern(string value) =>
        $"%{value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)}%";

    private sealed record SummaryRow(int TotalCount, decimal TotalAmount);

    private sealed record PageRow(
        long PayoutId,
        long OperatorUserId,
        string CompanyName,
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        decimal GrossRevenue,
        decimal CommissionAmount,
        decimal NetAmount,
        DateTimeOffset? RequestedAtUtc,
        string Status);
}

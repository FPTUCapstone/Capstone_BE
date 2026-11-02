using System.Globalization;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Payouts.GetList;

namespace TripMate.Application.Features.Admin.Payouts.Common;

/// <summary>The filter a payout list is read with. The caller (Staff review or Administrator
/// settlement) has already authorized the actor and validated the status against its own scope.</summary>
public sealed record PayoutListFilter(
    string? Keyword,
    string Status,
    DateOnly? PeriodFrom,
    DateOnly? PeriodTo,
    int PageNumber,
    int PageSize);

/// <summary>
/// Shared, read-only persisted-snapshot list used by UC-63 (Staff review) and UC-65 (Administrator
/// settlement queue). It never recalculates amounts and exposes no beneficiary, reviewer, or
/// gateway data.
/// </summary>
public static class PayoutListReader
{
    public static async Task<PayoutsResponseDto> ReadAsync(
        IApplicationDbContext dbContext,
        PayoutListFilter filter,
        CancellationToken cancellationToken)
    {
        var filtered = dbContext.Payouts
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var pattern = ContainsPattern(filter.Keyword.Trim());
            filtered = filtered.Where(payout =>
                EF.Functions.Like(payout.PayoutCode, pattern, "\\") ||
                EF.Functions.Like(payout.Operator.CompanyName, pattern, "\\"));
        }

        filtered = filtered.Where(payout => payout.Status == filter.Status);

        if (filter.PeriodFrom.HasValue)
        {
            filtered = filtered.Where(payout => payout.PeriodEnd >= filter.PeriodFrom.Value);
        }

        if (filter.PeriodTo.HasValue)
        {
            filtered = filtered.Where(payout => payout.PeriodStart <= filter.PeriodTo.Value);
        }

        var summary = await filtered
            .GroupBy(_ => 1)
            .Select(group => new SummaryRow(
                group.Count(),
                group.Sum(payout => payout.NetAmount)))
            .FirstOrDefaultAsync(cancellationToken) ?? new SummaryRow(0, 0m);

        var totalCount = summary.TotalCount;

        var skipLong = ((long)filter.PageNumber - 1) * filter.PageSize;
        var pageRows = skipLong > int.MaxValue
            ? []
            : await filtered
                .OrderByDescending(payout => payout.PeriodStart)
                .ThenByDescending(payout => payout.OperatorUserId)
                .ThenByDescending(payout => payout.Id)
                .Skip((int)skipLong)
                .Take(filter.PageSize)
                .Select(payout => new PageRow(
                    payout.Id,
                    payout.PayoutCode,
                    payout.OperatorUserId,
                    payout.Operator.CompanyName,
                    payout.PeriodStart,
                    payout.PeriodEnd,
                    payout.PeriodCount,
                    payout.GrossRevenue,
                    payout.CommissionAmount,
                    payout.NetAmount,
                    payout.RequestedAtUtc,
                    payout.Status))
                .ToListAsync(cancellationToken);

        var totalPages = filter.PageSize > 0
            ? (int)Math.Ceiling(totalCount / (double)filter.PageSize)
            : 0;

        return new PayoutsResponseDto(
            filter.PageNumber,
            filter.PageSize,
            totalCount,
            totalPages,
            summary.TotalAmount,
            pageRows.Select(row => new PayoutListItemDto(
                row.PayoutId.ToString(CultureInfo.InvariantCulture),
                row.PayoutCode,
                new PayoutOperatorDto(
                    row.OperatorUserId.ToString(CultureInfo.InvariantCulture),
                    row.CompanyName),
                row.PeriodStart,
                row.PeriodEnd,
                row.PeriodCount,
                row.GrossRevenue,
                row.CommissionAmount,
                row.NetAmount,
                row.RequestedAtUtc,
                row.Status)).ToList());
    }

    private static string ContainsPattern(string value) =>
        $"%{value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)}%";

    private sealed record SummaryRow(
        int TotalCount,
        decimal TotalAmount);

    private sealed record PageRow(
        long PayoutId,
        string PayoutCode,
        long OperatorUserId,
        string CompanyName,
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        int PeriodCount,
        decimal GrossRevenue,
        decimal CommissionAmount,
        decimal NetAmount,
        DateTimeOffset? RequestedAtUtc,
        string Status);
}
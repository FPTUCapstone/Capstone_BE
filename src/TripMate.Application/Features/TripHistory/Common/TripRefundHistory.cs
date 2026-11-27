using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>Durable refund obligations classify history without rewriting service or payment facts.</summary>
public static class TripRefundHistory
{
    public static IQueryable<long> BookingIds(IApplicationDbContext db) => db.DirectRefundCases.Select(c => c.BookingId)
        .Union(db.Refunds.Where(r => r.Status != RefundStatus.Rejected).Select(r => r.BookingId));

    public sealed record State(bool Pending, bool Disputed, decimal CompletedAmount)
    {
        public string DisplayStatus(decimal total) => Disputed ? "RefundDisputed" : Pending ? "RefundPending"
            : CompletedAmount >= total ? "Refunded" : "PartiallyRefunded";
    }

    public static async Task<Dictionary<long, State>> LoadAsync(IApplicationDbContext db, long[] ids, CancellationToken ct)
    {
        var direct = await db.DirectRefundCases.AsNoTracking().Where(c => ids.Contains(c.BookingId))
            .Select(c => new { c.BookingId, Amount = (decimal)c.RefundAmountVnd, Done = c.Status == DirectRefundCase.StatusCompleted, Disputed = c.Status == DirectRefundCase.StatusDisputed }).ToListAsync(ct);
        var legacy = await db.Refunds.AsNoTracking().Where(r => ids.Contains(r.BookingId) && r.Status != RefundStatus.Rejected)
            .Select(r => new { r.BookingId, r.Amount, Done = r.Status == RefundStatus.Processed, Disputed = false }).ToListAsync(ct);
        return direct.Concat(legacy).GroupBy(r => r.BookingId).ToDictionary(g => g.Key,
            g => new State(g.Any(r => !r.Done), g.Any(r => r.Disputed), g.Where(r => r.Done).Sum(r => r.Amount)));
    }
}
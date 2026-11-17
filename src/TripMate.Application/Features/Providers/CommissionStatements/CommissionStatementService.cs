using Microsoft.EntityFrameworkCore;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Providers.CommissionStatements;

public interface ICommissionStatementService
{
    Task<StatementGenerationSummaryDto> GenerateStatementsAsync(string? targetPeriod = null, CancellationToken cancellationToken = default);
    Task<OverdueProcessingSummaryDto> ProcessOverdueStatementsAsync(DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default);
}

public sealed class CommissionStatementService : ICommissionStatementService
{
    private readonly IApplicationDbContext _context;
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public CommissionStatementService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<StatementGenerationSummaryDto> GenerateStatementsAsync(string? targetPeriod = null, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowVn = nowUtc.ToOffset(VietnamOffset);

        // Determine target period (YYYY-MM). Default to previous calendar month in Vietnam time.
        string period;
        int targetYear;
        int targetMonth;

        if (!string.IsNullOrWhiteSpace(targetPeriod))
        {
            var parts = targetPeriod.Trim().Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], out targetYear) || !int.TryParse(parts[1], out targetMonth) || targetMonth < 1 || targetMonth > 12)
            {
                throw new ArgumentException("Invalid target period format. Expected YYYY-MM.", nameof(targetPeriod));
            }
            period = $"{targetYear:D4}-{targetMonth:D2}";
        }
        else
        {
            var prevMonthDate = nowVn.DateTime.AddMonths(-1);
            targetYear = prevMonthDate.Year;
            targetMonth = prevMonthDate.Month;
            period = $"{targetYear:D4}-{targetMonth:D2}";
        }

        var startOfTargetMonthVn = new DateTime(targetYear, targetMonth, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var endOfTargetMonthVn = startOfTargetMonthVn.AddMonths(1);

        var startUtc = new DateTimeOffset(startOfTargetMonthVn, VietnamOffset).ToUniversalTime();
        var endUtc = new DateTimeOffset(endOfTargetMonthVn, VietnamOffset).ToUniversalTime();

        // Query eligible completed bookings in the target period that have not yet been assigned to a statement
        var eligibleBookings = await _context.ServiceBookings
            .Include(b => b.Service)
            .Where(b => b.Status == ServiceBooking.Completed
                        && b.CommissionStatementId == null
                        && b.OutcomeAtUtc != null
                        && b.OutcomeAtUtc >= startUtc
                        && b.OutcomeAtUtc < endUtc)
            .ToListAsync(cancellationToken);

        if (eligibleBookings.Count == 0)
        {
            return new StatementGenerationSummaryDto(period, 0, 0, 0m);
        }

        // Group bookings directly by Service.ProviderId
        var bookingsByProvider = eligibleBookings
            .GroupBy(b => b.Service.ProviderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Load existing statements for this period to ensure idempotency
        var existingProviderIds = await _context.ServiceCommissionStatements
            .Where(s => s.Period == period)
            .Select(s => s.ProviderId)
            .ToListAsync(cancellationToken);

        var existingSet = new HashSet<long>(existingProviderIds);

        var todayVn = DateOnly.FromDateTime(nowVn.DateTime);
        var issueDate = todayVn;
        var dueDate = issueDate.AddDays(7); // BR-199: payable within 7 days of issue

        int generatedCount = 0;
        int skippedCount = 0;
        decimal totalCommission = 0m;

        foreach (var (providerId, bookings) in bookingsByProvider)
        {
            if (existingSet.Contains(providerId))
            {
                skippedCount++;
                continue;
            }

            var totalRequests = bookings.Count;
            var totalActual = bookings.Sum(b => b.ActualAmount ?? 0m);
            // BR-198: 10% of each completed request rounded down to whole VND
            var commission = bookings.Sum(b => b.CommissionAmount ?? decimal.Floor((b.ActualAmount ?? 0m) * 0.10m));

            var statementCode = $"STMT-{period.Replace("-", "")}-P{providerId:D4}";

            var statement = ServiceCommissionStatement.Create(
                statementCode: statementCode,
                providerId: providerId,
                period: period,
                totalRequests: totalRequests,
                totalActualAmount: totalActual,
                commissionAmount: commission,
                issueDate: issueDate,
                dueDate: dueDate);

            _context.ServiceCommissionStatements.Add(statement);
            await _context.SaveChangesAsync(cancellationToken);

            foreach (var b in bookings)
            {
                b.AssignCommissionStatement(statement.Id);
            }

            generatedCount++;
            totalCommission += commission;
            existingSet.Add(providerId);
        }

        if (generatedCount > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new StatementGenerationSummaryDto(period, generatedCount, skippedCount, totalCommission);
    }

    public async Task<OverdueProcessingSummaryDto> ProcessOverdueStatementsAsync(DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        var effectiveNowUtc = nowUtc ?? DateTimeOffset.UtcNow;
        var nowVn = effectiveNowUtc.ToOffset(VietnamOffset);
        var todayVn = DateOnly.FromDateTime(nowVn.DateTime);

        // 1. Mark statements past due date as Overdue (BR-199, §3.10.7)
        var pastDueStatements = await _context.ServiceCommissionStatements
            .Where(s => (s.Status == ServiceCommissionStatement.StatusIssued || s.Status == ServiceCommissionStatement.StatusPaymentReported)
                        && todayVn > s.DueDate)
            .ToListAsync(cancellationToken);

        int markedOverdueCount = 0;
        foreach (var statement in pastDueStatements)
        {
            statement.MarkOverdue();
            markedOverdueCount++;
        }

        // 2. Lock accounts for statements 15 days overdue (BR-200, §3.10.7)
        // A statement is >= 15 days overdue when todayVn >= DueDate.AddDays(15) and Status != Paid
        var overdueThresholdStatements = await _context.ServiceCommissionStatements
            .Include(s => s.Provider)
                .ThenInclude(p => p.User)
            .Where(s => s.Status != ServiceCommissionStatement.StatusPaid
                        && todayVn >= s.DueDate.AddDays(15))
            .ToListAsync(cancellationToken);

        int accountsLockedCount = 0;
        var lockedUsers = new HashSet<long>();

        foreach (var statement in overdueThresholdStatements)
        {
            var user = statement.Provider?.User;
            if (user is not null && user.Status != AccountStatus.Locked && lockedUsers.Add(user.Id))
            {
                user.Lock($"The account has been locked because the commission statement for {statement.Period} is unpaid 15 days after its due date (BR-200).", effectiveNowUtc);

                var audit = new AuditLog(
                    actorUserId: null,
                    actionType: AuditActionTypes.ServiceProviderAutoLocked,
                    affectedEntity: "User",
                    affectedEntityId: user.Id,
                    createdAtUtc: effectiveNowUtc,
                    beforeData: $"Status={AccountStatus.Active}",
                    afterData: $"Status={AccountStatus.Locked}; Reason=Overdue commission statement {statement.StatementCode} for period {statement.Period}");

                _context.AuditLogs.Add(audit);
                accountsLockedCount++;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new OverdueProcessingSummaryDto(markedOverdueCount, accountsLockedCount);
    }
}

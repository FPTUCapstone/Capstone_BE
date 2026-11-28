using System.Globalization;

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
    public const string ProviderAutoLockNotificationType = "MSG210";
    public const string OverdueReminderNotificationType = "CommissionStatementOverdueReminder";

    private readonly IApplicationDbContext _context;
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly CultureInfo VietnamCulture = CultureInfo.GetCultureInfo("vi-VN");

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

    public Task<OverdueProcessingSummaryDto> ProcessOverdueStatementsAsync(
        DateTimeOffset? nowUtc = null, CancellationToken cancellationToken = default)
    {
        var effectiveNowUtc = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var attempted = false;
        return _context.ExecuteInSerializableTransactionAsync(ct =>
        {
            // The execution strategy can replay a transaction after a transient failure.
            if (attempted)
                _context.ClearTrackedEntities();
            attempted = true;
            return ProcessOverdueStatementsCoreAsync(effectiveNowUtc, ct);
        }, cancellationToken);
    }

    private async Task<OverdueProcessingSummaryDto> ProcessOverdueStatementsCoreAsync(
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var todayVn = DateOnly.FromDateTime(nowUtc.ToOffset(VietnamOffset).DateTime);

        // Mark unpaid balances overdue and queue the MSG207 reminder (BR-199, §3.10.7).
        var pastDueStatements = await _context.ServiceCommissionStatements
            .Include(s => s.Provider)
            .Where(s => s.CommissionAmount > 0
                        && (s.Status == ServiceCommissionStatement.StatusIssued || s.Status == ServiceCommissionStatement.StatusPaymentReported)
                        && todayVn > s.DueDate)
            .ToListAsync(cancellationToken);

        await MarkOverdueAndQueueRemindersAsync(pastDueStatements, nowUtc, cancellationToken);

        // 2. Lock accounts for statements 15 days overdue (BR-200, §3.10.7)
        // A statement is >= 15 days overdue when todayVn >= DueDate.AddDays(15) and Status != Paid
        var overdueThresholdStatements = await _context.ServiceCommissionStatements
            .Include(s => s.Provider)
                .ThenInclude(p => p.User)
            .Where(s => s.CommissionAmount > 0
                        && s.Status != ServiceCommissionStatement.StatusPaid
                        && todayVn >= s.DueDate.AddDays(ServiceCommissionStatement.AutoLockGraceDays)
                        && s.Provider.User != null && s.Provider.User.Status != AccountStatus.Locked)
            .OrderBy(s => s.DueDate)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);

        var statementsToLock = overdueThresholdStatements.DistinctBy(s => s.Provider.UserId).ToList();
        if (statementsToLock.Count > 0)
        {
            var providerUserIds = statementsToLock.Select(s => s.Provider.User!.Id).ToArray();
            var refreshTokens = await _context.RefreshTokens
                .Where(token => providerUserIds.Contains(token.UserId) && token.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var token in refreshTokens)
                token.RevokedAtUtc = nowUtc;

            var staffIds = await _context.Users.AsNoTracking()
                .Where(user => user.Role == UserRole.Staff && user.Status == AccountStatus.Active)
                .Select(user => user.Id)
                .ToListAsync(cancellationToken);

            foreach (var statement in statementsToLock)
            {
                var user = statement.Provider.User!;
                var previousStatus = user.Status;
                var reason = $"Commission statement {statement.StatementCode} for {statement.Period} is unpaid 15 days after its due date (BR-200).";
                user.Lock(reason, nowUtc);

                _context.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                    actorUserId: null,
                    actionType: AuditActionTypes.ServiceProviderAutoLocked,
                    affectedEntity: AuditEntityTypes.User,
                    affectedEntityId: user.Id,
                    createdAtUtc: nowUtc,
                    result: AuditOutcome.Success,
                    reason: reason,
                    beforeData: $"Status={previousStatus}",
                    afterData: $"Status={AccountStatus.Locked}"));

                QueueAutoLockNotice(user.Id, user.Id, nowUtc,
                    $"Your Service Provider account was locked because commission statement {statement.StatementCode} for {statement.Period} is still unpaid 15 days after its due date. Contact TripMate support for payment instructions if you cannot sign in. Staff can restore access after all overdue statements are paid.");
                foreach (var staffId in staffIds)
                    QueueAutoLockNotice(staffId, user.Id, nowUtc,
                        $"Service Provider {user.FullName} was locked because commission statement {statement.StatementCode} for {statement.Period} is still unpaid 15 days after its due date. The account can be unlocked after all overdue statements are paid.");
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new OverdueProcessingSummaryDto(pastDueStatements.Count, statementsToLock.Count);
    }

    private async Task MarkOverdueAndQueueRemindersAsync(
        IReadOnlyList<ServiceCommissionStatement> statements, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (statements.Count == 0) return;

        var statementIds = statements.Select(statement => statement.Id).ToArray();
        // Reporting payment after the due date can put an Overdue statement back in PaymentReported.
        var previouslyReminded = (await _context.Notifications.AsNoTracking()
            .Where(notice => notice.Type == OverdueReminderNotificationType
                && notice.RelatedEntityType == nameof(ServiceCommissionStatement)
                && notice.RelatedEntityId.HasValue && statementIds.Contains(notice.RelatedEntityId.Value))
            .Select(notice => notice.RelatedEntityId!.Value)
            .ToListAsync(ct)).ToHashSet();

        foreach (var statement in statements)
        {
            statement.MarkOverdue();
            if (statement.Provider.UserId is not long recipientId || previouslyReminded.Contains(statement.Id))
                continue;

            var amount = statement.CommissionAmount.ToString("N0", VietnamCulture);
            var dueDate = statement.DueDate.ToString("dd/MM/yyyy", VietnamCulture);
            _context.Notifications.Add(new Notification
            {
                UserId = recipientId,
                Channel = NotificationChannel.Email,
                Type = OverdueReminderNotificationType,
                Title = "Commission statement overdue",
                Body = $"Commission statement {statement.StatementCode} for {statement.Period} ({amount} VND) was due on {dueDate}. Check your commission statement in TripMate. If you already transferred the money, contact support with the transfer reference before sending another payment.",
                RelatedEntityType = nameof(ServiceCommissionStatement),
                RelatedEntityId = statement.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = nowUtc,
            });
        }
    }

    private void QueueAutoLockNotice(long recipientId, long providerUserId, DateTimeOffset nowUtc, string body) =>
        _context.Notifications.Add(new Notification
        {
            UserId = recipientId,
            Channel = NotificationChannel.Email,
            Type = ProviderAutoLockNotificationType,
            Title = "Service Provider account locked",
            Body = body,
            RelatedEntityType = AuditEntityTypes.User,
            RelatedEntityId = providerUserId,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = nowUtc,
        });
}
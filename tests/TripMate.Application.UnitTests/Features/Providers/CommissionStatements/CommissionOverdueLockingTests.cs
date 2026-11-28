using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Providers.CommissionStatements;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Providers.CommissionStatements;

public sealed class CommissionOverdueLockingTests
{
    private static (TestDbContext context, User user, ServiceProvider provider, ServiceCommissionStatement statement) SetupProviderWithStatement(
        DateOnly issueDate, DateOnly dueDate, decimal commissionAmount = 40_000m)
    {
        var context = TestDbContext.Create();

        var user = new User
        {
            Id = 300,
            FullName = "Overdue Provider",
            Email = "overdue_prov@test.com",
            PhoneNumber = "0908888888",
            PasswordHash = "hash",
            Role = UserRole.ServiceProvider,
            Status = AccountStatus.Active
        };

        var provider = ServiceProvider.Create(
            name: "Hue Ancient Garden",
            serviceCategory: CommercialService.CategoryRestaurant,
            status: "Active",
            contactEmail: "overdue_prov@test.com",
            contactPhone: "0908888888");
        provider.AssignAccount(user);
        typeof(ServiceProvider).GetProperty(nameof(ServiceProvider.Id))!.SetValue(provider, 30L);

        var statement = ServiceCommissionStatement.Create(
            "STMT-202609-P0030", provider.Id, "2026-09", 4, 400000m, commissionAmount,
            issueDate, dueDate);

        context.Users.Add(user);
        context.ServiceProviders.Add(provider);
        context.ServiceCommissionStatements.Add(statement);
        context.SaveChanges();

        return (context, user, provider, statement);
    }

    [Fact]
    public async Task Statements_past_due_date_transition_to_Overdue()
    {
        // Statement issued 2026-10-01, due 2026-10-08
        var (context, _, _, statement) = SetupProviderWithStatement(
            issueDate: DateOnly.Parse("2026-10-01"),
            dueDate: DateOnly.Parse("2026-10-08"));

        var service = new CommissionStatementService(context);

        // Current time is 2026-10-10 (2 days after due date)
        var nowUtc = DateTimeOffset.Parse("2026-10-10T08:00:00Z");
        var summary = await service.ProcessOverdueStatementsAsync(nowUtc);

        summary.MarkedOverdueCount.Should().Be(1);
        summary.AccountsLockedCount.Should().Be(0);

        var updated = await context.ServiceCommissionStatements.FindAsync(statement.Id);
        updated!.Status.Should().Be(ServiceCommissionStatement.StatusOverdue);
    }

    [Fact]
    public async Task Due_date_reminder_is_queued_once_even_if_payment_reporting_reopens_overdue_transition()
    {
        var (context, user, _, statement) = SetupProviderWithStatement(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8));
        await using var db = context;
        var service = new CommissionStatementService(db);
        var firstOverdue = new DateTimeOffset(2026, 10, 8, 17, 0, 0, TimeSpan.Zero);

        (await service.ProcessOverdueStatementsAsync(firstOverdue.AddTicks(-1))).MarkedOverdueCount.Should().Be(0);
        db.Notifications.Should().BeEmpty();
        (await service.ProcessOverdueStatementsAsync(firstOverdue)).MarkedOverdueCount.Should().Be(1);
        (await service.ProcessOverdueStatementsAsync(firstOverdue)).MarkedOverdueCount.Should().Be(0);

        statement.ReportPayment("FT-UNCONFIRMED", firstOverdue);
        await db.SaveChangesAsync();
        (await service.ProcessOverdueStatementsAsync(firstOverdue.AddMinutes(10))).MarkedOverdueCount.Should().Be(1);

        var reminder = db.Notifications.Should().ContainSingle().Which;
        reminder.Type.Should().Be("CommissionStatementOverdueReminder");
        reminder.UserId.Should().Be(user.Id);
        reminder.RelatedEntityId.Should().Be(statement.Id);
        reminder.Channel.Should().Be(NotificationChannel.Email);
        reminder.Body.Should().Contain(statement.StatementCode).And.Contain("40.000");
        reminder.CreatedAtUtc.Should().Be(firstOverdue);
        user.Status.Should().Be(AccountStatus.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zero_commission_neither_locks_the_provider_nor_accepts_a_payment_report(bool legacyOverdue)
    {
        var (context, user, _, statement) = SetupProviderWithStatement(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8), commissionAmount: 0m);
        await using var db = context;
        if (legacyOverdue)
        {
            statement.MarkOverdue();
            await db.SaveChangesAsync();
        }

        var originalStatus = statement.Status;
        var service = new CommissionStatementService(db);
        var summary = await service.ProcessOverdueStatementsAsync(
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        summary.MarkedOverdueCount.Should().Be(0);
        summary.AccountsLockedCount.Should().Be(0);
        statement.Status.Should().Be(originalStatus);
        user.Status.Should().Be(AccountStatus.Active);
        db.Notifications.Should().BeEmpty();

        var report = await new ReportCommissionPaymentCommandHandler(db).HandleAsync(
            new ReportCommissionPaymentCommand(statement.Id, user.Id, "FT-ZERO"));
        report.IsFailure.Should().BeTrue();
        report.ErrorCode.Should().Be(CommissionStatementErrorCodes.InvalidState);
        statement.Status.Should().Be(originalStatus);
    }

    [Fact]
    public async Task Provider_account_is_automatically_locked_when_commission_is_15_days_overdue()
    {
        // Statement issued 2026-10-01, due 2026-10-08.
        // 15 days overdue = 2026-10-08 + 15 days = 2026-10-23
        var (context, user, _, statement) = SetupProviderWithStatement(
            issueDate: DateOnly.Parse("2026-10-01"),
            dueDate: DateOnly.Parse("2026-10-08"));

        var service = new CommissionStatementService(context);

        // Before 15 days overdue: 2026-10-22
        var dayBeforeLock = DateTimeOffset.Parse("2026-10-22T08:00:00Z");
        var preSummary = await service.ProcessOverdueStatementsAsync(dayBeforeLock);
        preSummary.AccountsLockedCount.Should().Be(0);
        user.Status.Should().Be(AccountStatus.Active);

        // Exactly on or after 15 days overdue: 2026-10-23
        var lockDay = DateTimeOffset.Parse("2026-10-23T08:00:00Z");
        var lockSummary = await service.ProcessOverdueStatementsAsync(lockDay);

        lockSummary.AccountsLockedCount.Should().Be(1);

        var lockedUser = await context.Users.FindAsync(user.Id);
        lockedUser!.Status.Should().Be(AccountStatus.Locked);
        lockedUser.LockReason.Should().Contain("BR-200");

        // Verify BR-200 AuditLog was written
        var audit = await context.AuditLogs
            .FirstOrDefaultAsync(a => a.ActionType == AuditActionTypes.ServiceProviderAutoLocked && a.AffectedEntityId == user.Id);
        audit.Should().NotBeNull();
        audit!.AfterData.Should().Contain("Locked");
        audit.Result.Should().Be(AuditOutcome.Success);
        audit.Reason.Should().Be(lockedUser.LockReason);
    }

    [Fact]
    public async Task Settled_paid_statements_do_not_trigger_account_locking()
    {
        var (context, user, _, statement) = SetupProviderWithStatement(
            issueDate: DateOnly.Parse("2026-10-01"),
            dueDate: DateOnly.Parse("2026-10-08"));

        // Statement is Paid
        statement.ReportPayment("FT2610050001", DateTimeOffset.Parse("2026-10-05T08:00:00Z"));
        statement.ConfirmReceipt(staffUserId: 999, confirmedAtUtc: DateTimeOffset.Parse("2026-10-06T08:00:00Z"), receiptNumber: "REC-202609-000001");
        await context.SaveChangesAsync();

        var service = new CommissionStatementService(context);

        // 30 days after due date
        var wayPastDue = DateTimeOffset.Parse("2026-11-15T08:00:00Z");
        var summary = await service.ProcessOverdueStatementsAsync(wayPastDue);

        summary.AccountsLockedCount.Should().Be(0);
        summary.MarkedOverdueCount.Should().Be(0);

        var checkedUser = await context.Users.FindAsync(user.Id);
        checkedUser!.Status.Should().Be(AccountStatus.Active);
    }

    [Fact]
    public async Task Overdue_processing_is_retry_safe_and_idempotent()
    {
        var (context, user, _, _) = SetupProviderWithStatement(
            issueDate: DateOnly.Parse("2026-10-01"),
            dueDate: DateOnly.Parse("2026-10-08"));

        var service = new CommissionStatementService(context);
        var lockDay = DateTimeOffset.Parse("2026-10-23T08:00:00Z");

        // First execution locks account
        var run1 = await service.ProcessOverdueStatementsAsync(lockDay);
        run1.AccountsLockedCount.Should().Be(1);

        // Second execution on same state should not double-lock or error
        var run2 = await service.ProcessOverdueStatementsAsync(lockDay);
        run2.AccountsLockedCount.Should().Be(0);

        var checkedUser = await context.Users.FindAsync(user.Id);
        checkedUser!.Status.Should().Be(AccountStatus.Locked);
    }

    [Fact]
    public async Task Automatic_lock_revokes_all_refresh_tokens_and_queues_one_msg210_per_recipient()
    {
        var (context, providerUser, provider, _) = SetupProviderWithStatement(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8));
        await using var db = context;
        var activeStaff = new User { Id = 301, FullName = "Active Staff", Email = "staff@example.test", Role = UserRole.Staff, Status = AccountStatus.Active };
        var inactiveStaff = new User { Id = 302, FullName = "Inactive Staff", Email = "inactive@example.test", Role = UserRole.Staff, Status = AccountStatus.Inactive };
        db.Users.AddRange(activeStaff, inactiveStaff);
        var lockTime = DateTimeOffset.Parse("2026-10-22T17:00:00Z"); // Midnight of the 15th day in Vietnam.
        var previousRevocation = lockTime.AddDays(-1);
        db.RefreshTokens.AddRange(
            new RefreshToken { UserId = providerUser.Id, TokenHash = "provider-session-1", ExpiresAtUtc = lockTime.AddDays(1) },
            new RefreshToken { UserId = providerUser.Id, TokenHash = "provider-session-2", ExpiresAtUtc = lockTime.AddDays(1) },
            new RefreshToken { UserId = providerUser.Id, TokenHash = "previously-revoked", ExpiresAtUtc = lockTime.AddDays(1), RevokedAtUtc = previousRevocation },
            new RefreshToken { UserId = activeStaff.Id, TokenHash = "staff-session", ExpiresAtUtc = lockTime.AddDays(1) });
        db.ServiceCommissionStatements.Add(ServiceCommissionStatement.Create(
            "STMT-202608-P0030", provider.Id, "2026-08", 1, 100000m, 10000m,
            new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 5)));
        await db.SaveChangesAsync();
        var previousSessionVersion = providerUser.SessionVersion;
        var service = new CommissionStatementService(db);

        (await service.ProcessOverdueStatementsAsync(lockTime)).AccountsLockedCount.Should().Be(1);
        (await service.ProcessOverdueStatementsAsync(lockTime)).AccountsLockedCount.Should().Be(0);

        providerUser.SessionVersion.Should().Be(previousSessionVersion + 1);
        var tokens = await db.RefreshTokens.ToDictionaryAsync(token => token.TokenHash);
        tokens["provider-session-1"].RevokedAtUtc.Should().Be(lockTime);
        tokens["provider-session-2"].RevokedAtUtc.Should().Be(lockTime);
        tokens["previously-revoked"].RevokedAtUtc.Should().Be(previousRevocation);
        tokens["staff-session"].RevokedAtUtc.Should().BeNull();

        var notices = await db.Notifications.Where(notice => notice.Type == "MSG210").ToListAsync();
        notices.Select(notice => notice.UserId).Should().BeEquivalentTo(new[] { providerUser.Id, activeStaff.Id });
        notices.Should().OnlyContain(notice => notice.Channel == NotificationChannel.Email
            && notice.Type == "MSG210" && notice.Status == NotificationStatus.Pending
            && notice.RelatedEntityId == providerUser.Id);
        notices.Single(notice => notice.UserId == providerUser.Id).Body.Should().Contain("2026-08");
        notices.Single(notice => notice.UserId == activeStaff.Id).Body.Should().Contain(providerUser.FullName);
        (await db.AuditLogs.CountAsync(audit => audit.ActionType == AuditActionTypes.ServiceProviderAutoLocked
            && audit.AffectedEntityId == providerUser.Id)).Should().Be(1);
    }
}
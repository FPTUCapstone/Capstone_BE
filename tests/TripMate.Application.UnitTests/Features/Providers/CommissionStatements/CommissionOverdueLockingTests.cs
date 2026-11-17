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
        DateOnly issueDate, DateOnly dueDate)
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
            "STMT-202609-P0030", provider.Id, "2026-09", 4, 400000m, 40000m,
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
}
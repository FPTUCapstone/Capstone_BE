using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Providers.CommissionStatements;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Providers.CommissionStatements;

public sealed class CommissionReconciliationTests
{
    private static (TestDbContext context, User staffUser, ServiceProvider provider, ServiceCommissionStatement statement) SetupReconciliationEnvironment()
    {
        var context = TestDbContext.Create();

        var staff = new User
        {
            Id = 200,
            FullName = "Staff Specialist",
            Email = "staff@tripmate.vn",
            PhoneNumber = "0909999999",
            PasswordHash = "hash",
            Role = UserRole.Staff,
            Status = AccountStatus.Active
        };

        var providerUser = new User
        {
            Id = 100,
            FullName = "Provider",
            Email = "prov@test.com",
            PhoneNumber = "0901111111",
            PasswordHash = "hash",
            Role = UserRole.ServiceProvider,
            Status = AccountStatus.Active
        };

        var provider = ServiceProvider.Create(
            name: "Da Nang Beach Bar",
            serviceCategory: CommercialService.CategoryRestaurant,
            status: "Active",
            contactEmail: "prov@test.com",
            contactPhone: "0901111111");
        provider.AssignAccount(providerUser);
        typeof(ServiceProvider).GetProperty(nameof(ServiceProvider.Id))!.SetValue(provider, 15L);

        var statement = ServiceCommissionStatement.Create(
            "STMT-202610-P0015", provider.Id, "2026-10", 5, 500000m, 50000m,
            DateOnly.Parse("2026-11-01"), DateOnly.Parse("2026-11-08"));

        // Provider reported payment
        statement.ReportPayment("FT2611037777", DateTimeOffset.Parse("2026-11-03T10:00:00Z"));

        context.Users.AddRange(staff, providerUser);
        context.ServiceProviders.Add(provider);
        context.ServiceCommissionStatements.Add(statement);
        context.SaveChanges();

        return (context, staff, provider, statement);
    }

    [Fact]
    public async Task Staff_can_reconcile_statement_generate_receipt_and_record_audit_log()
    {
        var (context, staff, provider, statement) = SetupReconciliationEnvironment();

        var queryHandler = new AdminReconciliationQueryHandler(context);
        var commissionService = new CommissionStatementService(context);
        var commandHandler = new AdminReconciliationCommandHandler(context, commissionService);

        // Staff lists statements
        var listResult = await queryHandler.HandleAsync(new GetReconciliationStatementsQuery(ProviderId: provider.Id));
        listResult.IsSuccess.Should().BeTrue();
        listResult.Value.Items.Should().HaveCount(1);
        listResult.Value.Items[0].Status.Should().Be(ServiceCommissionStatement.StatusPaymentReported);

        // Staff reconciles payment
        var reconcileResult = await commandHandler.HandleAsync(
            new ConfirmCommissionReceiptCommand(statement.Id, staff.Id, "Bank credit verified on MB statement",
                "FT2611037777", 50_000m));

        reconcileResult.IsSuccess.Should().BeTrue();
        reconcileResult.Value.Status.Should().Be(ServiceCommissionStatement.StatusPaid);
        reconcileResult.Value.ConfirmedByUserId.Should().Be(staff.Id);
        reconcileResult.Value.ConfirmedAtUtc.Should().NotBeNull();
        reconcileResult.Value.ReceiptNumber.Should().StartWith("REC-202610-");

        // Verify statement in DB
        var updated = await context.ServiceCommissionStatements.FindAsync(statement.Id);
        updated!.Status.Should().Be(ServiceCommissionStatement.StatusPaid);
        updated.ReceiptNumber.Should().Be(reconcileResult.Value.ReceiptNumber);

        // Verify BR-174 immutable AuditLog was written
        var audit = await context.AuditLogs
            .FirstOrDefaultAsync(a => a.ActionType == AuditActionTypes.CommissionReceiptConfirmed && a.AffectedEntityId == statement.Id);
        audit.Should().NotBeNull();
        audit!.ActorUserId.Should().Be(staff.Id);
        audit.AfterData.Should().Contain(reconcileResult.Value.ReceiptNumber);
    }

    [Fact]
    public async Task Reconciling_already_paid_statement_is_rejected_preventing_duplicate_receipts()
    {
        var (context, staff, provider, statement) = SetupReconciliationEnvironment();

        var commissionService = new CommissionStatementService(context);
        var commandHandler = new AdminReconciliationCommandHandler(context, commissionService);

        // First reconcile
        var first = await commandHandler.HandleAsync(
            new ConfirmCommissionReceiptCommand(statement.Id, staff.Id, BankTransferReference: "FT2611037777", ReceivedAmountVnd: 50_000m));
        first.IsSuccess.Should().BeTrue();

        // Second reconcile attempt -> AlreadyPaid
        var second = await commandHandler.HandleAsync(new ConfirmCommissionReceiptCommand(statement.Id, staff.Id));
        second.IsSuccess.Should().BeFalse();
        second.ErrorCode.Should().Be(CommissionStatementErrorCodes.AlreadyPaid);
    }

    [Fact]
    public async Task Staff_must_match_the_bank_credit_and_can_correct_the_reference_without_overwriting_the_report()
    {
        var (db, staff, _, statement) = SetupReconciliationEnvironment();
        await using var context = db;
        statement.MarkOverdue();
        await db.SaveChangesAsync();
        var handler = new AdminReconciliationCommandHandler(db, new CommissionStatementService(db));

        var shortPayment = await handler.HandleAsync(new ConfirmCommissionReceiptCommand(
            statement.Id, staff.Id, BankTransferReference: "CORRECTED-001", ReceivedAmountVnd: 49_999m));
        shortPayment.ErrorCode.Should().Be(CommissionStatementErrorCodes.ReceiptAmountMismatch);
        statement.Status.Should().Be(ServiceCommissionStatement.StatusOverdue);

        var paid = await handler.HandleAsync(new ConfirmCommissionReceiptCommand(
            statement.Id, staff.Id, BankTransferReference: " CORRECTED-001 ", ReceivedAmountVnd: 50_000m));
        paid.IsSuccess.Should().BeTrue();
        statement.BankTransferReference.Should().Be("FT2611037777");
        statement.VerifiedBankTransferReference.Should().Be("CORRECTED-001");
        db.AuditLogs.Single().AfterData.Should().Contain("CORRECTED-001");
    }

    [Fact]
    public async Task List_uses_statement_id_to_keep_equal_period_and_due_date_on_distinct_pages()
    {
        var (db, _, _, first) = SetupReconciliationEnvironment();
        await using var context = db;
        var otherProvider = ServiceProvider.Create("Second provider", CommercialService.CategoryRestaurant,
            ServiceProvider.StatusActive);
        db.ServiceProviders.Add(otherProvider);
        await db.SaveChangesAsync();
        var second = ServiceCommissionStatement.Create("STMT-SECOND", otherProvider.Id, first.Period,
            1, 500_000m, 50_000m, first.IssueDate, first.DueDate);
        db.ServiceCommissionStatements.Add(second);
        await db.SaveChangesAsync();
        var query = new AdminReconciliationQueryHandler(db);

        var pageOne = await query.HandleAsync(new GetReconciliationStatementsQuery(PageNumber: 1, PageSize: 1));
        var pageTwo = await query.HandleAsync(new GetReconciliationStatementsQuery(PageNumber: 2, PageSize: 1));
        pageOne.Value.Items.Should().ContainSingle().Which.StatementId.Should().Be(second.Id);
        pageTwo.Value.Items.Should().ContainSingle().Which.StatementId.Should().Be(first.Id);
    }
}
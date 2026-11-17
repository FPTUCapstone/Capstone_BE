using FluentAssertions;

using TripMate.Application.Features.Providers.CommissionStatements;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Providers.CommissionStatements;

public sealed class ProviderCommissionWorkflowTests
{
    private static (TestDbContext context, User user1, ServiceProvider provider1, User user2, ServiceProvider provider2) SetupTwoProviders()
    {
        var context = TestDbContext.Create();

        var u1 = new User
        {
            Id = 101,
            FullName = "Provider One",
            Email = "prov1@test.com",
            PhoneNumber = "0900000001",
            PasswordHash = "hash",
            Role = UserRole.ServiceProvider,
            Status = AccountStatus.Active
        };
        var p1 = ServiceProvider.Create(
            name: "Provider One Co",
            serviceCategory: CommercialService.CategoryRestaurant,
            status: "Active",
            contactEmail: "prov1@test.com",
            contactPhone: "0900000001");
        p1.AssignAccount(u1);
        typeof(ServiceProvider).GetProperty(nameof(ServiceProvider.Id))!.SetValue(p1, 11L);

        var u2 = new User
        {
            Id = 102,
            FullName = "Provider Two",
            Email = "prov2@test.com",
            PhoneNumber = "0900000002",
            PasswordHash = "hash",
            Role = UserRole.ServiceProvider,
            Status = AccountStatus.Active
        };
        var p2 = ServiceProvider.Create(
            name: "Provider Two Co",
            serviceCategory: CommercialService.CategoryVehicle,
            status: "Active",
            contactEmail: "prov2@test.com",
            contactPhone: "0900000002");
        p2.AssignAccount(u2);
        typeof(ServiceProvider).GetProperty(nameof(ServiceProvider.Id))!.SetValue(p2, 12L);

        context.Users.AddRange(u1, u2);
        context.ServiceProviders.AddRange(p1, p2);
        context.SaveChanges();

        return (context, u1, p1, u2, p2);
    }

    [Fact]
    public async Task Provider_can_view_own_statements_and_details()
    {
        var (context, u1, p1, u2, p2) = SetupTwoProviders();

        var stmt1 = ServiceCommissionStatement.Create(
            "STMT-202610-P0011", p1.Id, "2026-10", 3, 300000m, 30000m,
            DateOnly.Parse("2026-11-01"), DateOnly.Parse("2026-11-08"));
        context.ServiceCommissionStatements.Add(stmt1);
        await context.SaveChangesAsync();

        var queryHandler = new ProviderCommissionQueryHandler(context);

        // List statements
        var listResult = await queryHandler.HandleAsync(new GetProviderStatementsQuery(u1.Id));
        listResult.IsSuccess.Should().BeTrue();
        listResult.Value.Items.Should().HaveCount(1);
        listResult.Value.Items[0].StatementCode.Should().Be("STMT-202610-P0011");

        // Detail statement
        var detailResult = await queryHandler.HandleAsync(new GetProviderStatementDetailQuery(stmt1.Id, u1.Id));
        detailResult.IsSuccess.Should().BeTrue();
        detailResult.Value.StatementCode.Should().Be("STMT-202610-P0011");
        detailResult.Value.BankDetails.AccountNumber.Should().Be("999988887777");
        detailResult.Value.BankDetails.TransferSyntax.Should().Be("TRIPMATE STMT-202610-P0011");
    }

    [Fact]
    public async Task Provider_cannot_view_or_modify_another_providers_statement()
    {
        var (context, u1, p1, u2, p2) = SetupTwoProviders();

        var stmt1 = ServiceCommissionStatement.Create(
            "STMT-202610-P0011", p1.Id, "2026-10", 3, 300000m, 30000m,
            DateOnly.Parse("2026-11-01"), DateOnly.Parse("2026-11-08"));
        context.ServiceCommissionStatements.Add(stmt1);
        await context.SaveChangesAsync();

        var queryHandler = new ProviderCommissionQueryHandler(context);
        var reportHandler = new ReportCommissionPaymentCommandHandler(context);

        // Provider 2 attempts to view Provider 1's statement -> Forbidden
        var detailResult = await queryHandler.HandleAsync(new GetProviderStatementDetailQuery(stmt1.Id, u2.Id));
        detailResult.IsSuccess.Should().BeFalse();
        detailResult.ErrorCode.Should().Be(CommissionStatementErrorCodes.Forbidden);

        // Provider 2 attempts to report payment for Provider 1's statement -> Forbidden
        var reportResult = await reportHandler.HandleAsync(new ReportCommissionPaymentCommand(stmt1.Id, u2.Id, "FT2611019999"));
        reportResult.IsSuccess.Should().BeFalse();
        reportResult.ErrorCode.Should().Be(CommissionStatementErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Reporting_payment_requires_valid_reference_and_transitions_status_to_PaymentReported()
    {
        var (context, u1, p1, _, _) = SetupTwoProviders();

        var stmt1 = ServiceCommissionStatement.Create(
            "STMT-202610-P0011", p1.Id, "2026-10", 3, 300000m, 30000m,
            DateOnly.Parse("2026-11-01"), DateOnly.Parse("2026-11-08"));
        context.ServiceCommissionStatements.Add(stmt1);
        await context.SaveChangesAsync();

        var reportHandler = new ReportCommissionPaymentCommandHandler(context);

        // Empty bank reference rejected
        var emptyRefResult = await reportHandler.HandleAsync(new ReportCommissionPaymentCommand(stmt1.Id, u1.Id, "   "));
        emptyRefResult.IsSuccess.Should().BeFalse();
        emptyRefResult.ErrorCode.Should().Be(CommissionStatementErrorCodes.MissingBankReference);

        // Valid bank reference accepted
        var successResult = await reportHandler.HandleAsync(new ReportCommissionPaymentCommand(stmt1.Id, u1.Id, "FT2611028888"));
        successResult.IsSuccess.Should().BeTrue();
        successResult.Value.Status.Should().Be(ServiceCommissionStatement.StatusPaymentReported);
        successResult.Value.BankTransferReference.Should().Be("FT2611028888");
        successResult.Value.ReportedAtUtc.Should().NotBeNull();

        // Cannot report again when already PaymentReported
        var secondReport = await reportHandler.HandleAsync(new ReportCommissionPaymentCommand(stmt1.Id, u1.Id, "FT2611029999"));
        secondReport.IsSuccess.Should().BeFalse();
        secondReport.ErrorCode.Should().Be(CommissionStatementErrorCodes.InvalidState);
    }
}
using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Providers.CommissionStatements;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Providers.CommissionStatements;

public sealed class CommissionStatementGenerationTests
{
    private static (TestDbContext context, ServiceProvider provider, CommercialService service) SetupTestEnvironment()
    {
        var context = TestDbContext.Create();

        var user = new User
        {
            Id = 100,
            FullName = "Provider User",
            Email = "provider1@test.com",
            PhoneNumber = "0900000001",
            PasswordHash = "hash",
            Role = UserRole.ServiceProvider,
            Status = AccountStatus.Active
        };
        context.Users.Add(user);

        var provider = ServiceProvider.Create(
            name: "Hoi An Riverside Cafe",
            serviceCategory: CommercialService.CategoryRestaurant,
            status: "Active",
            contactEmail: "provider1@test.com",
            contactPhone: "0900000001");
        provider.AssignAccount(user);
        typeof(ServiceProvider).GetProperty(nameof(ServiceProvider.Id))!.SetValue(provider, 10L);
        context.ServiceProviders.Add(provider);

        var service = CommercialService.Create(
            provider: provider,
            serviceCategory: CommercialService.CategoryRestaurant,
            name: "Morning Coffee Set",
            priceAmount: 50000m,
            priceUnit: CommercialService.PriceUnitPerPerson,
            availabilityStatus: CommercialService.AvailabilityAvailable,
            currencyCode: "VND",
            priceIncludesTax: true,
            refundableDepositAmount: null,
            coverImageUrl: null,
            attributesJson: null,
            lastUpdatedAtUtc: DateTimeOffset.UtcNow);
        typeof(CommercialService).GetProperty(nameof(CommercialService.Id))!.SetValue(service, 50L);
        context.CommercialServices.Add(service);

        context.SaveChanges();
        return (context, provider, service);
    }

    [Fact]
    public async Task Generates_single_statement_for_eligible_completed_bookings_in_target_period()
    {
        var (context, provider, service) = SetupTestEnvironment();

        // Create completed bookings in 2026-10 (target period)
        var b1 = ServiceBooking.CreatePendingRequest(
            travelerUserId: 501,
            serviceId: service.Id,
            startAtUtc: DateTimeOffset.Parse("2026-10-10T03:00:00Z"),
            endAtUtc: null,
            quantity: 2,
            estimatedAmount: 100000m,
            submittedAtUtc: DateTimeOffset.Parse("2026-10-09T03:00:00Z"));
        b1.TryDecide(ServiceBooking.Confirmed, null, 100, DateTimeOffset.Parse("2026-10-09T04:00:00Z"));
        b1.TryRecordOutcome(ServiceBooking.Completed, 100000m, 100, DateTimeOffset.Parse("2026-10-10T17:00:00Z"));

        var b2 = ServiceBooking.CreatePendingRequest(
            travelerUserId: 502,
            serviceId: service.Id,
            startAtUtc: DateTimeOffset.Parse("2026-10-20T03:00:00Z"),
            endAtUtc: null,
            quantity: 1,
            estimatedAmount: 50000m,
            submittedAtUtc: DateTimeOffset.Parse("2026-10-19T03:00:00Z"));
        b2.TryDecide(ServiceBooking.Confirmed, null, 100, DateTimeOffset.Parse("2026-10-19T04:00:00Z"));
        b2.TryRecordOutcome(ServiceBooking.Completed, 50000m, 100, DateTimeOffset.Parse("2026-10-20T17:00:00Z"));

        // Booking outside period (in 2026-09)
        var b3 = ServiceBooking.CreatePendingRequest(
            travelerUserId: 503,
            serviceId: service.Id,
            startAtUtc: DateTimeOffset.Parse("2026-09-25T03:00:00Z"),
            endAtUtc: null,
            quantity: 1,
            estimatedAmount: 50000m,
            submittedAtUtc: DateTimeOffset.Parse("2026-09-24T03:00:00Z"));
        b3.TryDecide(ServiceBooking.Confirmed, null, 100, DateTimeOffset.Parse("2026-09-24T04:00:00Z"));
        b3.TryRecordOutcome(ServiceBooking.Completed, 50000m, 100, DateTimeOffset.Parse("2026-09-25T17:00:00Z"));

        context.ServiceBookings.AddRange(b1, b2, b3);
        await context.SaveChangesAsync();

        var serviceHandler = new CommissionStatementService(context);
        var summary = await serviceHandler.GenerateStatementsAsync("2026-10");

        summary.Period.Should().Be("2026-10");
        summary.GeneratedCount.Should().Be(1);
        summary.TotalCommissionAmount.Should().Be(15000m); // 10,000 + 5,000

        var statement = await context.ServiceCommissionStatements.FirstOrDefaultAsync(s => s.Period == "2026-10");
        statement.Should().NotBeNull();
        statement!.ProviderId.Should().Be(provider.Id);
        statement.TotalRequests.Should().Be(2);
        statement.TotalActualAmount.Should().Be(150000m);
        statement.CommissionAmount.Should().Be(15000m);
        statement.Status.Should().Be(ServiceCommissionStatement.StatusIssued);
        statement.DueDate.Should().Be(statement.IssueDate.AddDays(7));

        // Bookings in target period should be linked to statement
        b1.CommissionStatementId.Should().Be(statement.Id);
        b2.CommissionStatementId.Should().Be(statement.Id);
        b3.CommissionStatementId.Should().BeNull();
    }

    [Fact]
    public async Task Generation_is_idempotent_and_prevents_duplicates()
    {
        var (context, provider, service) = SetupTestEnvironment();

        var b1 = ServiceBooking.CreatePendingRequest(
            travelerUserId: 501,
            serviceId: service.Id,
            startAtUtc: DateTimeOffset.Parse("2026-10-10T03:00:00Z"),
            endAtUtc: null,
            quantity: 2,
            estimatedAmount: 100000m,
            submittedAtUtc: DateTimeOffset.Parse("2026-10-09T03:00:00Z"));
        b1.TryDecide(ServiceBooking.Confirmed, null, 100, DateTimeOffset.Parse("2026-10-09T04:00:00Z"));
        b1.TryRecordOutcome(ServiceBooking.Completed, 100000m, 100, DateTimeOffset.Parse("2026-10-10T17:00:00Z"));

        context.ServiceBookings.Add(b1);
        await context.SaveChangesAsync();

        var serviceHandler = new CommissionStatementService(context);

        // First run
        var firstSummary = await serviceHandler.GenerateStatementsAsync("2026-10");
        firstSummary.GeneratedCount.Should().Be(1);

        // Second run (repeated / concurrent retry simulation)
        var secondSummary = await serviceHandler.GenerateStatementsAsync("2026-10");
        secondSummary.GeneratedCount.Should().Be(0);

        // Verify exactly one statement exists in DB
        var statements = await context.ServiceCommissionStatements.Where(s => s.Period == "2026-10").ToListAsync();
        statements.Should().HaveCount(1);
    }

    [Fact]
    public async Task Does_not_generate_statement_when_provider_has_no_completed_bookings()
    {
        var (context, provider, service) = SetupTestEnvironment();

        // Only Rejected or NoShow bookings
        var b1 = ServiceBooking.CreatePendingRequest(
            travelerUserId: 501,
            serviceId: service.Id,
            startAtUtc: DateTimeOffset.Parse("2026-10-10T03:00:00Z"),
            endAtUtc: null,
            quantity: 2,
            estimatedAmount: 100000m,
            submittedAtUtc: DateTimeOffset.Parse("2026-10-09T03:00:00Z"));
        b1.TryDecide(ServiceBooking.Rejected, "No capacity", 100, DateTimeOffset.Parse("2026-10-09T04:00:00Z"));

        context.ServiceBookings.Add(b1);
        await context.SaveChangesAsync();

        var serviceHandler = new CommissionStatementService(context);
        var summary = await serviceHandler.GenerateStatementsAsync("2026-10");

        summary.GeneratedCount.Should().Be(0);
        var statements = await context.ServiceCommissionStatements.ToListAsync();
        statements.Should().BeEmpty();
    }
}
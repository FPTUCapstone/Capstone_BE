using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Api.IntegrationTests.Tickets;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Bookings;

/// <summary>
/// UC-42 read endpoints for FE (Q-FE2, spec v1.2 §3.3): the same list/detail endpoint serves
/// Staff's "Refund Return Reconciliation" screen (filtered to ReturnReported) and the
/// Administrator's "Escalated Refunds Monitor" (filtered to Escalated/Reconciliation). SQL
/// Server (TEAM_ENGINEERING_RULES §11-13).
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class GetAdminRefundsSqlServerTests
{
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task GetRefunds_FiltersByMultipleStatusesAtOnce()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var reconciliationBookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long reconciliationRefundId = await SeedReconciliationRefundAsync(database, reconciliationBookingId, seed.TravelerId, 900_000m);
        var pendingBookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        await SeedPendingOperatorConfirmationRefundAsync(database, pendingBookingId, seed.TravelerId, 500_000m);

        var result = await HandleListAsync(database, nameof(UserRole.Staff),
            new GetAdminRefundsQuery(Status: [nameof(RefundStatus.Reconciliation), nameof(RefundStatus.Escalated)]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(item => item.RefundId == reconciliationRefundId);
    }

    [SqlServerFact]
    public async Task GetRefunds_ByTourOperator_IsUnauthorized()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);

        var result = await HandleListAsync(database, nameof(UserRole.TourOperator), new GetAdminRefundsQuery());

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.Unauthorized);
    }

    [SqlServerFact]
    public async Task GetRefundDetail_DoesNotExist_IsNotFound()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);

        var result = await HandleDetailAsync(database, nameof(UserRole.Administrator), new GetAdminRefundDetailQuery(999_999));

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.NotFound);
    }

    [SqlServerFact]
    public async Task GetRefundDetail_ByAdministrator_ReturnsFullDetail()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedReconciliationRefundAsync(database, bookingId, seed.TravelerId, 900_000m);

        var result = await HandleDetailAsync(database, nameof(UserRole.Administrator), new GetAdminRefundDetailQuery(refundId));

        result.IsSuccess.Should().BeTrue();
        result.Value.RefundId.Should().Be(refundId);
        result.Value.Status.Should().Be(nameof(RefundStatus.Reconciliation));
    }

    private static async Task<Result<AdminRefundsResponseDto>> HandleListAsync(SqlServerTestDatabase database,
        string role, GetAdminRefundsQuery query)
    {
        await using var context = database.CreateDbContext();
        var handler = new GetAdminRefundsQueryHandler(context, new CurrentUser(1, role));
        return await handler.Handle(query, CancellationToken.None);
    }

    private static async Task<Result<AdminRefundDetailDto>> HandleDetailAsync(SqlServerTestDatabase database,
        string role, GetAdminRefundDetailQuery query)
    {
        await using var context = database.CreateDbContext();
        var handler = new GetAdminRefundDetailQueryHandler(context, new CurrentUser(1, role));
        return await handler.Handle(query, CancellationToken.None);
    }

    private static async Task<long> SeedPendingOperatorConfirmationRefundAsync(SqlServerTestDatabase database,
        long bookingId, long travelerId, decimal amount)
    {
        await using var context = database.CreateDbContext();
        var payment = PaymentTransaction.Create(bookingId, PaymentGateway.Mock, amount,
            orderCode: 840_000 + bookingId, createdAtUtc: Now.AddDays(-5));
        payment.MarkSuccess("gw-test", null, Now.AddDays(-5), 0m);
        context.PaymentTransactions.Add(payment);
        await context.SaveChangesAsync();

        var operation = BookingCancellationOperation.Create(bookingId, travelerId,
            BookingCancellationActor.Traveler, Guid.NewGuid(), requestHash: "test-hash",
            refundAmount: amount, refundStatus: nameof(RefundStatus.PendingOperatorConfirmation), Now.AddDays(-5));
        context.BookingCancellationOperations.Add(operation);
        await context.SaveChangesAsync();

        var refund = Refund.CreatePendingOperatorConfirmation(bookingId, payment.Id, operation.Id, amount,
            "Traveler cancelled", RefundInitiator.System, Now.AddDays(-5));
        context.Refunds.Add(refund);
        await context.SaveChangesAsync();
        return refund.Id;
    }

    private static async Task<long> SeedReconciliationRefundAsync(SqlServerTestDatabase database, long bookingId,
        long travelerId, decimal amount)
    {
        await using var context = database.CreateDbContext();
        var payment = PaymentTransaction.Create(bookingId, PaymentGateway.Mock, amount - 100_000m,
            orderCode: 850_000 + bookingId, createdAtUtc: Now.AddDays(-5));
        payment.MarkSuccess("gw-test", null, Now.AddDays(-5), 0m);
        context.PaymentTransactions.Add(payment);
        await context.SaveChangesAsync();

        var operation = BookingCancellationOperation.Create(bookingId, travelerId,
            BookingCancellationActor.Traveler, Guid.NewGuid(), requestHash: "test-hash-mismatch",
            refundAmount: amount, refundStatus: nameof(RefundStatus.Reconciliation), Now.AddDays(-5));
        context.BookingCancellationOperations.Add(operation);
        await context.SaveChangesAsync();

        var refund = Refund.CreateReconciliation(bookingId, payment.Id, operation.Id, amount,
            "Amount mismatch", RefundInitiator.System, Now.AddDays(-5));
        context.Refunds.Add(refund);
        await context.SaveChangesAsync();
        return refund.Id;
    }

    private sealed class CurrentUser(long userId, string role) : ICurrentUserService
    {
        public long? UserId => userId;

        public string? Role => role;
    }
}
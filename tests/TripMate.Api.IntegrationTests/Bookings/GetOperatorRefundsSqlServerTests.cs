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
/// UC-42 read endpoints for FE (Q-FE2, spec v1.2 §3.3): a Tour Operator's own "Confirm Booking
/// Refund" list/detail, scoped strictly to bookings of tours it owns. SQL Server
/// (TEAM_ENGINEERING_RULES §11-13).
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class GetOperatorRefundsSqlServerTests
{
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task GetRefunds_OnlyReturnsRefundsOfTheSignedInOperatorsOwnTours()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var ownBookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long ownRefundId = await SeedPendingOperatorConfirmationRefundAsync(database, ownBookingId, seed.TravelerId, 500_000m);

        var otherSeed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var otherBookingId = await otherSeed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        await SeedPendingOperatorConfirmationRefundAsync(database, otherBookingId, otherSeed.TravelerId, 300_000m);

        var result = await HandleListAsync(database, seed.OperatorId,
            new GetOperatorRefundsQuery(Status: nameof(RefundStatus.PendingOperatorConfirmation)));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(item => item.RefundId == ownRefundId);
        result.Value.TotalCount.Should().Be(1);
    }

    [SqlServerFact]
    public async Task GetRefunds_FiltersByStatus()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        await SeedPendingOperatorConfirmationRefundAsync(database, bookingId, seed.TravelerId, 500_000m);

        var result = await HandleListAsync(database, seed.OperatorId,
            new GetOperatorRefundsQuery(Status: nameof(RefundStatus.AwaitingPartnerReturn)));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(0);
    }

    [SqlServerFact]
    public async Task GetRefundDetail_ByAnotherOperator_IsForbidden()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedPendingOperatorConfirmationRefundAsync(database, bookingId, seed.TravelerId, 500_000m);
        long otherOperatorId = await AddTourOperatorAsync(database);

        var result = await HandleDetailAsync(database, otherOperatorId, new GetOperatorRefundDetailQuery(refundId));

        result.ErrorCode.Should().Be(RefundConfirmationErrorCodes.OperatorNotOwner);
    }

    [SqlServerFact]
    public async Task GetRefundDetail_DoesNotExist_IsNotFound()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);

        var result = await HandleDetailAsync(database, seed.OperatorId, new GetOperatorRefundDetailQuery(999_999));

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.NotFound);
    }

    [SqlServerFact]
    public async Task GetRefundDetail_ByOwningOperator_ReturnsFullDetail()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedPendingOperatorConfirmationRefundAsync(database, bookingId, seed.TravelerId, 500_000m);

        var result = await HandleDetailAsync(database, seed.OperatorId, new GetOperatorRefundDetailQuery(refundId));

        result.IsSuccess.Should().BeTrue();
        result.Value.RefundId.Should().Be(refundId);
        result.Value.Amount.Should().Be(500_000m);
        result.Value.Status.Should().Be(nameof(RefundStatus.PendingOperatorConfirmation));
    }

    private static async Task<Result<OperatorRefundsResponseDto>> HandleListAsync(SqlServerTestDatabase database,
        long operatorUserId, GetOperatorRefundsQuery query)
    {
        await using var context = database.CreateDbContext();
        var handler = new GetOperatorRefundsQueryHandler(context, new CurrentUser(operatorUserId, nameof(UserRole.TourOperator)));
        return await handler.Handle(query, CancellationToken.None);
    }

    private static async Task<Result<OperatorRefundDetailDto>> HandleDetailAsync(SqlServerTestDatabase database,
        long operatorUserId, GetOperatorRefundDetailQuery query)
    {
        await using var context = database.CreateDbContext();
        var handler = new GetOperatorRefundDetailQueryHandler(context, new CurrentUser(operatorUserId, nameof(UserRole.TourOperator)));
        return await handler.Handle(query, CancellationToken.None);
    }

    private static async Task<long> SeedPendingOperatorConfirmationRefundAsync(SqlServerTestDatabase database,
        long bookingId, long travelerId, decimal amount)
    {
        await using var context = database.CreateDbContext();
        var payment = PaymentTransaction.Create(bookingId, PaymentGateway.Mock, amount,
            orderCode: 830_000 + bookingId, createdAtUtc: Now.AddDays(-5));
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

    private static async Task<long> AddTourOperatorAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var otherOperator = new User
        {
            Email = $"other-operator-{Guid.NewGuid():N}@test.local",
            FullName = "Other Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        context.Users.Add(otherOperator);
        await context.SaveChangesAsync();
        return otherOperator.Id;
    }

    private sealed class CurrentUser(long userId, string role) : ICurrentUserService
    {
        public long? UserId => userId;

        public string? Role => role;
    }
}
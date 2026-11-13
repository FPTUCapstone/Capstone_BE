using FluentAssertions;

using Microsoft.EntityFrameworkCore;

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
/// UC-42 step 3 (SRS 3.8.4.3, spec v1.2): the Tour Operator confirms a refund that UC-41 created
/// in PendingOperatorConfirmation. Proves ownership/state checks and the BR-139 share split on
/// SQL Server (TEAM_ENGINEERING_RULES §11-13).
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class ConfirmBookingRefundSqlServerTests
{
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero); // a Friday

    [SqlServerFact]
    public async Task Confirm_ByAnotherTourOperator_IsForbidden()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedPendingOperatorConfirmationRefundAsync(database, bookingId, seed.TravelerId, amount: 1_000_000m);
        long otherOperatorId = await AddTourOperatorAsync(database);

        var result = await HandleAsync(database, otherOperatorId, Now, new ConfirmBookingRefundCommand(refundId));

        result.ErrorCode.Should().Be(RefundConfirmationErrorCodes.OperatorNotOwner);
        await using var context = database.CreateDbContext();
        (await context.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId)).Status
            .Should().Be(RefundStatus.PendingOperatorConfirmation);
    }

    [SqlServerFact]
    public async Task Confirm_RefundNotPendingOperatorConfirmation_IsConflict()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedPendingOperatorConfirmationRefundAsync(database, bookingId, seed.TravelerId, amount: 1_000_000m);
        await MarkConfirmedAsync(database, refundId, Now.AddDays(-1));

        var result = await HandleAsync(database, seed.OperatorId, Now, new ConfirmBookingRefundCommand(refundId));

        result.ErrorCode.Should().Be(RefundConfirmationErrorCodes.RequestNotPending);
    }

    [SqlServerFact]
    public async Task Confirm_WithNoSettlementTransferredYet_SkipsStraightToPending()
    {
        // BR-139: zero share (4.b1) skips AwaitingPartnerReturn entirely.
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedPendingOperatorConfirmationRefundAsync(database, bookingId, seed.TravelerId, amount: 1_000_000m);

        var result = await HandleAsync(database, seed.OperatorId, Now, new ConfirmBookingRefundCommand(refundId));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(RefundStatus.Pending));
        result.Value.OperatorReturnShare.Should().Be(0m);
        result.Value.ReturnDeadlineAtUtc.Should().BeNull();
        await using var context = database.CreateDbContext();
        var refund = await context.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId);
        refund.Status.Should().Be(RefundStatus.Pending);
        refund.NextAttemptAtUtc.Should().Be(Now);
    }

    [SqlServerFact]
    public async Task Confirm_AfterAdvanceSettlement_MovesToAwaitingPartnerReturnWithThreeWorkingDayDeadline()
    {
        // BR-139: 70% of PaidAmount already transferred -> 70% of the refund is the operator's
        // share. BR-144: the return deadline is 3 WORKING days after confirmation; 2026-11-13 is
        // a Friday, so the deadline skips the weekend (Sat 14 / Sun 15) and lands on Wed 18.
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedPendingOperatorConfirmationRefundAsync(database, bookingId, seed.TravelerId, amount: 500_000m);
        await SeedSettledAdvancePayoutAsync(database, bookingId, seed.OperatorId, payoutAmount: 700_000m);

        var result = await HandleAsync(database, seed.OperatorId, Now, new ConfirmBookingRefundCommand(refundId));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(RefundStatus.AwaitingPartnerReturn));
        result.Value.OperatorReturnShare.Should().Be(350_000m);
        result.Value.ReturnDeadlineAtUtc.Should().Be(new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero));
        await using var context = database.CreateDbContext();
        var refund = await context.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId);
        refund.Status.Should().Be(RefundStatus.AwaitingPartnerReturn);
        refund.NextAttemptAtUtc.Should().BeNull();
        (await context.AuditLogs.AsNoTracking().CountAsync(a => a.AffectedEntityId == refundId && a.AffectedEntity == "Refund"))
            .Should().Be(1);
    }

    [SqlServerFact]
    public async Task Confirm_RefundDoesNotExist_IsNotFound()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);

        var result = await HandleAsync(database, seed.OperatorId, Now, new ConfirmBookingRefundCommand(999_999));

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.NotFound);
    }

    private static async Task<Result<ConfirmBookingRefundResponse>> HandleAsync(SqlServerTestDatabase database,
        long operatorUserId, DateTimeOffset now, ConfirmBookingRefundCommand command)
    {
        await using var context = database.CreateDbContext();
        var handler = new ConfirmBookingRefundCommandHandler(context,
            new CurrentUser(operatorUserId, nameof(UserRole.TourOperator)), new ConfirmClock(now));
        return await handler.Handle(command, CancellationToken.None);
    }

    private static async Task<long> SeedPendingOperatorConfirmationRefundAsync(SqlServerTestDatabase database,
        long bookingId, long travelerId, decimal amount)
    {
        await using var context = database.CreateDbContext();
        var payment = PaymentTransaction.Create(bookingId, PaymentGateway.Mock, amount,
            orderCode: 800_000 + bookingId, createdAtUtc: Now.AddDays(-5));
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

    private static async Task MarkConfirmedAsync(SqlServerTestDatabase database, long refundId, DateTimeOffset confirmedAtUtc)
    {
        await using var context = database.CreateDbContext();
        var refund = await context.Refunds.SingleAsync(r => r.Id == refundId);
        refund.Confirm(0m, null, confirmedAtUtc);
        await context.SaveChangesAsync();
    }

    private static async Task SeedSettledAdvancePayoutAsync(SqlServerTestDatabase database, long bookingId,
        long operatorUserId, decimal payoutAmount)
    {
        var runDate = DateOnly.FromDateTime(Now.UtcDateTime.AddDays(-3));
        await using var context = database.CreateDbContext();
        var batch = PayoutBatch.Create(runDate, operatorUserId, Now.AddDays(-3));
        context.PayoutBatches.Add(batch);
        await context.SaveChangesAsync();

        var settlement = PayoutSettlement.Create(batch.Id, bookingId, operatorUserId, runDate,
            PayoutSettlementKind.Advance, grossAmount: payoutAmount, commissionRate: 0m, commissionAmount: 0m,
            netAmount: payoutAmount, debtOffsetAmount: 0m, payoutAmount: payoutAmount, Now.AddDays(-3));
        context.PayoutSettlements.Add(settlement);
        batch.AddSettlement(settlement);
        batch.MarkSettled("PAYOUT-TEST-REF", Now.AddDays(-3));
        await context.SaveChangesAsync();
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

    private sealed class ConfirmClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
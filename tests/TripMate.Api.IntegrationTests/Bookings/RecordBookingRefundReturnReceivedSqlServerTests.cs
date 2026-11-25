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
/// UC-42 steps 7-8 (SRS 3.8.4.3, spec v1.2): a Staff member reconciles the TripMate bank
/// statement and records the operator's full return share as received, making the refund
/// eligible for the UC-67 automatic gateway sweep. SQL Server (TEAM_ENGINEERING_RULES §11-13).
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class RecordBookingRefundReturnReceivedSqlServerTests
{
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task Record_ByNonStaffUser_IsUnauthorized()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedReturnReportedRefundAsync(database, bookingId, seed.TravelerId, amount: 500_000m,
            share: 350_000m);

        var result = await HandleAsync(database, seed.OperatorId, nameof(UserRole.TourOperator), Now,
            new RecordBookingRefundReturnReceivedCommand(refundId));

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.Unauthorized);
    }

    [SqlServerFact]
    public async Task Record_RefundNotReturnReported_IsConflict()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedPendingOperatorConfirmationRefundAsync(database, bookingId, seed.TravelerId, 500_000m);
        long staffId = await AddStaffUserAsync(database);

        var result = await HandleAsync(database, staffId, nameof(UserRole.Staff), Now,
            new RecordBookingRefundReturnReceivedCommand(refundId));

        result.ErrorCode.Should().Be(RefundConfirmationErrorCodes.RequestNotPending);
    }

    [SqlServerFact]
    public async Task Record_ByStaff_MovesToPendingReadyForGatewaySweep()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedReturnReportedRefundAsync(database, bookingId, seed.TravelerId, amount: 500_000m,
            share: 350_000m);
        long staffId = await AddStaffUserAsync(database);

        var result = await HandleAsync(database, staffId, nameof(UserRole.Staff), Now,
            new RecordBookingRefundReturnReceivedCommand(refundId));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(RefundStatus.Pending));
        await using var context = database.CreateDbContext();
        var refund = await context.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId);
        refund.Status.Should().Be(RefundStatus.Pending);
        refund.NextAttemptAtUtc.Should().Be(Now);
        (await context.AuditLogs.AsNoTracking().CountAsync(a => a.AffectedEntityId == refundId && a.AffectedEntity == "Refund"))
            .Should().Be(1);
    }

    private static async Task<Result<RecordBookingRefundReturnReceivedResponse>> HandleAsync(SqlServerTestDatabase database,
        long userId, string role, DateTimeOffset now, RecordBookingRefundReturnReceivedCommand command)
    {
        await using var context = database.CreateDbContext();
        var handler = new RecordBookingRefundReturnReceivedCommandHandler(context,
            new CurrentUser(userId, role), new RecordClock(now));
        return await handler.Handle(command, CancellationToken.None);
    }

    private static async Task<long> SeedPendingOperatorConfirmationRefundAsync(SqlServerTestDatabase database,
        long bookingId, long travelerId, decimal amount)
    {
        await using var context = database.CreateDbContext();
        var payment = PaymentTransaction.Create(bookingId, PaymentGateway.Mock, amount,
            orderCode: 820_000 + bookingId, createdAtUtc: Now.AddDays(-5));
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

    private static async Task<long> SeedReturnReportedRefundAsync(SqlServerTestDatabase database, long bookingId,
        long travelerId, decimal amount, decimal share)
    {
        long refundId = await SeedPendingOperatorConfirmationRefundAsync(database, bookingId, travelerId, amount);
        await using var context = database.CreateDbContext();
        var refund = await context.Refunds.SingleAsync(r => r.Id == refundId);
        refund.Confirm(share, Now.AddDays(3), Now.AddDays(-4));
        refund.ReportReturn("VCB-99999", Now.AddDays(-1));
        await context.SaveChangesAsync();
        return refundId;
    }

    private static async Task<long> AddStaffUserAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var staff = new User
        {
            Email = $"staff-{Guid.NewGuid():N}@test.local",
            FullName = "Staff Member",
            Role = UserRole.Staff,
            Status = AccountStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        context.Users.Add(staff);
        await context.SaveChangesAsync();
        return staff.Id;
    }

    private sealed class CurrentUser(long userId, string role) : ICurrentUserService
    {
        public long? UserId => userId;

        public string? Role => role;
    }

    private sealed class RecordClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
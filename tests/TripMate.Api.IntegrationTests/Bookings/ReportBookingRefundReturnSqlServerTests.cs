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
/// UC-42 step 5 (SRS 3.8.4.3, spec v1.2): the Tour Operator reports its bank transfer of the
/// confirmed return share. Proves the MSG172 missing-reference rejection and ownership/state
/// checks on SQL Server (TEAM_ENGINEERING_RULES §11-13).
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class ReportBookingRefundReturnSqlServerTests
{
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task Report_WithoutReturnReference_IsRejected()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedAwaitingPartnerReturnRefundAsync(database, bookingId, seed.TravelerId, amount: 500_000m,
            share: 350_000m);

        var result = await HandleAsync(database, seed.OperatorId, Now,
            new ReportBookingRefundReturnCommand(refundId, "   "));

        result.ErrorCode.Should().Be(RefundConfirmationErrorCodes.ReturnReferenceRequired);
        await using var context = database.CreateDbContext();
        (await context.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId)).Status
            .Should().Be(RefundStatus.AwaitingPartnerReturn);
    }

    [SqlServerFact]
    public async Task Report_ByAnotherTourOperator_IsForbidden()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedAwaitingPartnerReturnRefundAsync(database, bookingId, seed.TravelerId, amount: 500_000m,
            share: 350_000m);
        long otherOperatorId = await AddTourOperatorAsync(database);

        var result = await HandleAsync(database, otherOperatorId, Now,
            new ReportBookingRefundReturnCommand(refundId, "VCB-12345"));

        result.ErrorCode.Should().Be(RefundConfirmationErrorCodes.OperatorNotOwner);
    }

    [SqlServerFact]
    public async Task Report_RefundNotAwaitingPartnerReturn_IsConflict()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedAwaitingPartnerReturnRefundAsync(database, bookingId, seed.TravelerId, amount: 500_000m,
            share: 0m); // zero share skips straight to Pending, never AwaitingPartnerReturn.

        var result = await HandleAsync(database, seed.OperatorId, Now,
            new ReportBookingRefundReturnCommand(refundId, "VCB-12345"));

        result.ErrorCode.Should().Be(RefundConfirmationErrorCodes.RequestNotPending);
    }

    [SqlServerFact]
    public async Task Report_ByOwningOperatorWithReference_MovesToReturnReported()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        long refundId = await SeedAwaitingPartnerReturnRefundAsync(database, bookingId, seed.TravelerId, amount: 500_000m,
            share: 350_000m);

        var result = await HandleAsync(database, seed.OperatorId, Now,
            new ReportBookingRefundReturnCommand(refundId, " VCB-12345 "));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(RefundStatus.ReturnReported));
        result.Value.ReturnReference.Should().Be("VCB-12345");
        await using var context = database.CreateDbContext();
        var refund = await context.Refunds.AsNoTracking().SingleAsync(r => r.Id == refundId);
        refund.Status.Should().Be(RefundStatus.ReturnReported);
        refund.ReturnReference.Should().Be("VCB-12345");
        (await context.AuditLogs.AsNoTracking().CountAsync(a => a.AffectedEntityId == refundId && a.AffectedEntity == "Refund"))
            .Should().Be(1);
    }

    private static async Task<Result<ReportBookingRefundReturnResponse>> HandleAsync(SqlServerTestDatabase database,
        long operatorUserId, DateTimeOffset now, ReportBookingRefundReturnCommand command)
    {
        await using var context = database.CreateDbContext();
        var handler = new ReportBookingRefundReturnCommandHandler(context,
            new CurrentUser(operatorUserId, nameof(UserRole.TourOperator)), new ReportClock(now));
        return await handler.Handle(command, CancellationToken.None);
    }

    private static async Task<long> SeedAwaitingPartnerReturnRefundAsync(SqlServerTestDatabase database,
        long bookingId, long travelerId, decimal amount, decimal share)
    {
        await using var context = database.CreateDbContext();
        var payment = PaymentTransaction.Create(bookingId, PaymentGateway.Mock, amount,
            orderCode: 810_000 + bookingId, createdAtUtc: Now.AddDays(-5));
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
        refund.Confirm(share, share > 0m ? Now.AddDays(3) : null, Now.AddDays(-4));
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

    private sealed class ReportClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
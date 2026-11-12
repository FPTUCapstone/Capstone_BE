using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Api.IntegrationTests.Tickets;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Cancel;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Bookings;

/// <summary>
/// Proves on SQL Server what the in-memory provider cannot: the cancellation, seat release, refund and audit
/// commit in one serializable transaction, and the Idempotency-Key replay is enforced by the database.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class CancelBookingSqlServerTests
{
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task Cancel_WithinTwentyFourHoursOfPayment_CommitsCancellationAndSeatReleaseWithNoRefund()
    {
        // BR-157 retired 11/11/2026: a recent payment no longer grants a full refund regardless
        // of the BR-135 tier — 24h before departure is inside the under-10-days 0% tier, so this
        // now proves the same one-transaction commit (cancel + seat release + audit) at a 0%
        // outcome that requires MSG170 confirmation, rather than a full-refund outcome.
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var now = Departure.AddHours(-24);
        await AddSuccessfulPaymentAsync(database, bookingId, paidAtUtc: now.AddHours(-2));
        var reservedBefore = await ReservedCapacityAsync(database, seed.ScheduleId);

        var result = await HandleAsync(database, seed.TravelerId, nameof(UserRole.Traveler), now,
            new CancelBookingCommand(bookingId, Guid.NewGuid(), "ChangeOfPlans", "Cannot travel", Confirmed: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.RefundAmount.Should().Be(0m);
        await using var context = database.CreateDbContext();
        (await context.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == bookingId)).Status
            .Should().Be(BookingStatus.Cancelled);
        (await context.Refunds.AsNoTracking().CountAsync(refund => refund.BookingId == bookingId)).Should().Be(0);
        (await context.BookingCancellationOperations.AsNoTracking().CountAsync(operation => operation.BookingId == bookingId))
            .Should().Be(1);
        (await ReservedCapacityAsync(database, seed.ScheduleId)).Should().Be(reservedBefore - 1);
    }

    [SqlServerFact]
    public async Task Cancel_RepeatedIdempotencyKey_ReplaysOriginalAndRejectsChangedPayload()
    {
        // More than 14 days before departure (BR-135 v3: 100%) so this stays focused on the
        // idempotency-key replay behavior, independent of the 0%-tier confirmation requirement.
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var now = Departure.AddDays(-20);
        await AddSuccessfulPaymentAsync(database, bookingId, paidAtUtc: now.AddDays(-2));
        var key = Guid.NewGuid();

        var first = await HandleAsync(database, seed.TravelerId, nameof(UserRole.Traveler), now,
            new CancelBookingCommand(bookingId, key, "ChangeOfPlans", "Cannot travel"));
        var replay = await HandleAsync(database, seed.TravelerId, nameof(UserRole.Traveler), now,
            new CancelBookingCommand(bookingId, key, "ChangeOfPlans", "Cannot travel"));
        var changed = await HandleAsync(database, seed.TravelerId, nameof(UserRole.Traveler), now,
            new CancelBookingCommand(bookingId, key, "Different", "Different detail"));

        replay.IsSuccess.Should().BeTrue();
        replay.Value.RefundAmount.Should().Be(first.Value.RefundAmount);
        changed.ErrorCode.Should().Be(BookingCancellationErrorCodes.IdempotencyPayloadMismatch);
        await using var context = database.CreateDbContext();
        (await context.BookingCancellationOperations.AsNoTracking().CountAsync(operation => operation.BookingId == bookingId))
            .Should().Be(1);
        (await context.Refunds.AsNoTracking().CountAsync(refund => refund.BookingId == bookingId)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Cancel_InsideSeventyTwoHoursWithoutConfirmation_LeavesBookingUnchanged()
    {
        // 24h before departure lands in the under-10-days 0% tier under BR-135 v3 too (it was
        // inside 72h under v2), so this still needs — and still requires — MSG170 confirmation.
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var now = Departure.AddHours(-24);
        await AddSuccessfulPaymentAsync(database, bookingId, paidAtUtc: now.AddHours(-25));
        var reservedBefore = await ReservedCapacityAsync(database, seed.ScheduleId);

        var result = await HandleAsync(database, seed.TravelerId, nameof(UserRole.Traveler), now,
            new CancelBookingCommand(bookingId, Guid.NewGuid(), null, null));

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.ConfirmationRequired);
        await using var context = database.CreateDbContext();
        (await context.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == bookingId)).Status
            .Should().Be(BookingStatus.Confirmed);
        (await context.BookingCancellationOperations.AsNoTracking().CountAsync()).Should().Be(0);
        (await ReservedCapacityAsync(database, seed.ScheduleId)).Should().Be(reservedBefore);
    }

    [SqlServerFact]
    public async Task Cancel_ByAnotherTraveler_IsForbiddenAndLeavesBookingUnchanged()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var now = Departure.AddHours(-24);
        await AddSuccessfulPaymentAsync(database, bookingId, paidAtUtc: now.AddHours(-2));

        var result = await HandleAsync(database, seed.OtherTravelerId, nameof(UserRole.Traveler), now,
            new CancelBookingCommand(bookingId, Guid.NewGuid(), "ChangeOfPlans", "Cannot travel"));

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.Forbidden);
        await using var context = database.CreateDbContext();
        (await context.Bookings.AsNoTracking().SingleAsync(booking => booking.Id == bookingId)).Status
            .Should().Be(BookingStatus.Confirmed);
        (await context.BookingCancellationOperations.AsNoTracking().CountAsync()).Should().Be(0);
    }

    private static async Task<Result<CancelBookingResponse>> HandleAsync(SqlServerTestDatabase database, long userId,
        string role, DateTimeOffset now, CancelBookingCommand command)
    {
        await using var context = database.CreateDbContext();
        var handler = new CancelBookingCommandHandler(context,
            new CurrentUser(userId, role),
            new CancelClock(now), new SqlServerBookingCancellationPersistenceErrorClassifier());
        return await handler.Handle(command, CancellationToken.None);
    }

    private static async Task AddSuccessfulPaymentAsync(SqlServerTestDatabase database, long bookingId,
        DateTimeOffset paidAtUtc)
    {
        await using var context = database.CreateDbContext();
        var payment = PaymentTransaction.Create(bookingId, PaymentGateway.Mock, 1_000_000m,
            orderCode: 900_000 + bookingId, createdAtUtc: paidAtUtc.AddMinutes(-5));
        payment.MarkSuccess("gw-test", null, paidAtUtc, 0m);
        context.PaymentTransactions.Add(payment);
        await context.SaveChangesAsync();
    }

    private static async Task<int> ReservedCapacityAsync(SqlServerTestDatabase database, long scheduleId)
    {
        await using var context = database.CreateDbContext();
        return (await context.TourSchedules.AsNoTracking().SingleAsync(schedule => schedule.Id == scheduleId))
            .ReservedCapacity;
    }

    private sealed class CurrentUser(long userId, string role) : ICurrentUserService
    {
        public long? UserId => userId;

        public string? Role => role;
    }

    private sealed class CancelClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
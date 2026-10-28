using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Bookings.Cancel;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Cancel;

public sealed class CancelBookingCommandHandlerTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Handle_CouponSlotReleaseDependsOnPaymentAndReplayDoesNotReleaseTwice(
        bool paid, int expectedUsedCount)
    {
        await using var db = TestDbContext.Create();
        var now = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, now.AddDays(14), reservedCapacity: 1);
        var voucher = Voucher.Create(8, "SAVE10", VoucherDiscountType.Percentage, 10m,
            100_000m, 0m, 5, 1, now.AddDays(-2), now.AddDays(2), [tour], now.AddDays(-3));
        voucher.IncrementUsage();
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        db.Vouchers.Add(voucher);
        db.Users.Add(new User { Id = 7, Email = "traveler@test.local", FullName = "Traveler", Role = UserRole.Traveler });
        await db.SaveChangesAsync();

        var booking = Booking.Create("BK-20261101-00001", 7, schedule.Id, voucher.Id,
            1_000m, 100m, 900m,
            [BookingParticipant.Create("Traveler", ParticipantType.Adult, null, null, null, null, null)],
            now.AddDays(-1), now.AddDays(1));
        if (paid) booking.MarkPaid(now.AddHours(-1));
        db.Bookings.Add(booking);
        db.VoucherRedemptions.Add(VoucherRedemption.Create(voucher, booking, 100m, now.AddDays(-1)));
        await db.SaveChangesAsync();
        if (paid)
        {
            db.PaymentTransactions.Add(CreateSuccessfulPayment(51, booking.Id, booking.TotalAmount, now.AddHours(-1)));
            await db.SaveChangesAsync();
        }

        var handler = new CancelBookingCommandHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = now }, new NoIdempotencyConflict());
        var command = new CancelBookingCommand(booking.Id, Guid.NewGuid(), null, null);

        (await handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();

        db.ClearTrackedEntities();
        (await db.Vouchers.SingleAsync()).UsedCount.Should().Be(expectedUsedCount);
        (await db.Bookings.SingleAsync()).DiscountAmount.Should().Be(100m);
        (await db.VoucherRedemptions.CountAsync()).Should().Be(1);
        (await db.BookingCancellationOperations.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(7, 100)]
    [InlineData(3, 70)]
    [InlineData(1, 30)]
    public async Task Handle_NewBookingPersistsPolicySnapshotAndAppliesItsTier(int daysBeforeDeparture,
        decimal expectedPercent)
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, departure, reservedCapacity: 1);
        var booking = Booking.Create(7, schedule.Id, 1, 1_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        db.Bookings.Add(booking);
        db.Users.Add(new User { Id = 7, Email = "traveler@test.local", FullName = "Traveler", Role = UserRole.Traveler });
        await db.SaveChangesAsync();
        db.PaymentTransactions.Add(CreateSuccessfulPayment(51, booking.Id, booking.TotalAmount, departure.AddDays(-10)));
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();

        var handler = new CancelBookingCommandHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = departure.AddDays(-daysBeforeDeparture) }, new NoIdempotencyConflict());

        var result = await handler.Handle(new CancelBookingCommand(booking.Id, Guid.NewGuid(), null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RefundAmount.Should().Be(1_000m * expectedPercent / 100m);
        (await db.Bookings.SingleAsync()).CancellationPolicySnapshot
            .Should().Be(Booking.DefaultCancellationPolicySnapshot);
    }

    [Fact]
    public async Task Handle_TravelerCancellationSevenDaysBeforeDeparture_CancelsAndCreatesPendingRefund()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, departure, reservedCapacity: 2);
        var booking = Booking.Restore(41, 7, schedule.Id, 2, 800_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, TravelerCancellationPolicy.DefaultSnapshot);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        db.Bookings.Add(booking);
        db.Users.Add(new User { Id = 7, Email = "traveler@test.local", FullName = "Traveler", Role = UserRole.Traveler });
        db.PaymentTransactions.Add(CreateSuccessfulPayment(51, booking.Id, booking.TotalAmount, departure.AddDays(-10)));
        await db.SaveChangesAsync();
        var clock = new FakeDateTimeProvider { UtcNow = departure.AddDays(-7) };
        var command = new CancelBookingCommand(booking.Id, Guid.NewGuid(), "ChangeOfPlans", "Cannot travel");
        var handler = new CancelBookingCommandHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) }, clock, new NoIdempotencyConflict());

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RefundAmount.Should().Be(800_000m);
        (await db.Bookings.SingleAsync()).Status.Should().Be(BookingStatus.Cancelled);
        (await db.Refunds.SingleAsync()).Should().BeEquivalentTo(new
        {
            BookingId = booking.Id,
            Amount = 800_000m,
            Status = RefundStatus.Pending,
            InitiatedBy = RefundInitiator.System,
        });
        (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(0);
        db.RevokedTicketQrBookingIds.Should().ContainSingle().Which.Should().Be(booking.Id);
        (await db.AuditLogs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_SameIdempotencyKey_ReplaysExistingCancellationWithoutSecondRefund()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, departure, reservedCapacity: 1);
        var booking = Booking.Restore(41, 7, schedule.Id, 1, 500_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, TravelerCancellationPolicy.DefaultSnapshot);
        db.Tours.Add(tour); db.TourSchedules.Add(schedule); db.Bookings.Add(booking);
        db.Users.Add(new User { Id = 7, Email = "traveler@test.local", FullName = "Traveler", Role = UserRole.Traveler });
        db.PaymentTransactions.Add(CreateSuccessfulPayment(51, booking.Id, booking.TotalAmount, departure.AddDays(-10)));
        await db.SaveChangesAsync();
        var handler = new CancelBookingCommandHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = departure.AddDays(-7) }, new NoIdempotencyConflict());
        var command = new CancelBookingCommand(booking.Id, Guid.NewGuid(), null, null);

        var first = await handler.Handle(command, CancellationToken.None);
        var replay = await handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        replay.Should().BeEquivalentTo(first);
        (await db.Refunds.CountAsync()).Should().Be(1);
        (await db.BookingCancellationOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_ConfirmedBookingWithoutVerifiedPayment_DoesNotCancelOrCreateRefund()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, departure, reservedCapacity: 1);
        var booking = Booking.Restore(41, 7, schedule.Id, 1, 500_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, TravelerCancellationPolicy.DefaultSnapshot);
        db.Tours.Add(tour); db.TourSchedules.Add(schedule); db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var handler = new CancelBookingCommandHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = departure.AddDays(-7) }, new NoIdempotencyConflict());

        var result = await handler.Handle(new CancelBookingCommand(booking.Id, Guid.NewGuid(), null, null),
            CancellationToken.None);

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.PaymentNotRefundable);
        (await db.Bookings.SingleAsync()).Status.Should().Be(BookingStatus.Confirmed);
        (await db.Refunds.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_TourOperatorWithoutReason_ReturnsReasonRequired()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, departure, reservedCapacity: 1);
        var booking = Booking.Restore(41, 7, schedule.Id, 1, 500_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, TravelerCancellationPolicy.DefaultSnapshot);
        db.Tours.Add(tour); db.TourSchedules.Add(schedule); db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var handler = new CancelBookingCommandHandler(db,
            new FakeCurrentUserService { UserId = 8, Role = nameof(UserRole.TourOperator) },
            new FakeDateTimeProvider { UtcNow = departure.AddDays(-7) }, new NoIdempotencyConflict());

        var result = await handler.Handle(new CancelBookingCommand(booking.Id, Guid.NewGuid(), null, null),
            CancellationToken.None);

        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.ReasonRequired);
        (await db.Bookings.SingleAsync()).Status.Should().Be(BookingStatus.Confirmed);
    }

    [Fact]
    public async Task Handle_DeadlockVictim_RetriesBoundedlyAndReturnsTheDurableReplay()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, departure, reservedCapacity: 1);
        var booking = Booking.Restore(41, 7, schedule.Id, 1, 500_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, TravelerCancellationPolicy.DefaultSnapshot);
        db.Tours.Add(tour); db.TourSchedules.Add(schedule); db.Bookings.Add(booking);
        db.Users.Add(new User { Id = 7, Email = "traveler@test.local", FullName = "Traveler", Role = UserRole.Traveler });
        db.PaymentTransactions.Add(CreateSuccessfulPayment(51, booking.Id, booking.TotalAmount, departure.AddDays(-10)));
        await db.SaveChangesAsync();
        db.SerializableTransactionCompletionFailureFactory = attempt =>
            attempt == 1 ? new SimulatedDeadlockException() : null;
        var handler = new CancelBookingCommandHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = departure.AddDays(-7) }, new NoIdempotencyConflict());

        var result = await handler.Handle(new CancelBookingCommand(booking.Id, Guid.NewGuid(), null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.TransactionExecutionCount.Should().Be(2);
        (await db.BookingCancellationOperations.CountAsync()).Should().Be(1);
        (await db.Refunds.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenDeadlocksExhaustBoundedRetries_ReturnsRetryableFailure()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, departure, reservedCapacity: 1);
        var booking = Booking.Restore(41, 7, schedule.Id, 1, 500_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, TravelerCancellationPolicy.DefaultSnapshot);
        db.Tours.Add(tour); db.TourSchedules.Add(schedule); db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        db.SerializableTransactionCompletionFailureFactory = _ => new SimulatedDeadlockException();

        var handler = new CancelBookingCommandHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = departure.AddDays(-7) }, new NoIdempotencyConflict());

        var result = await handler.Handle(new CancelBookingCommand(booking.Id, Guid.NewGuid(), null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(BookingCancellationErrorCodes.ConcurrentRetryExhausted);
        db.TransactionExecutionCount.Should().Be(3);
    }

    private static Tour CreateTour(long id, long operatorUserId)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), id);
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), "Test tour");
        Set(tour, nameof(Tour.BasePrice), 100m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        return tour;
    }

    private static TourSchedule CreateSchedule(long id, Tour tour, DateTimeOffset departure, int reservedCapacity)
    {
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.Id), id);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), departure);
        Set(schedule, nameof(TourSchedule.EndAtUtc), departure.AddHours(8));
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.ReservedCapacity), reservedCapacity);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        return schedule;
    }

    private static PaymentTransaction CreateSuccessfulPayment(long id, long bookingId, decimal amount,
        DateTimeOffset createdAtUtc)
    {
        var transaction = (PaymentTransaction)Activator.CreateInstance(typeof(PaymentTransaction), nonPublic: true)!;
        Set(transaction, nameof(PaymentTransaction.Id), id);
        Set(transaction, nameof(PaymentTransaction.BookingId), bookingId);
        Set(transaction, nameof(PaymentTransaction.Amount), amount);
        Set(transaction, nameof(PaymentTransaction.TransactionType), PaymentTransaction.TypePayment);
        Set(transaction, nameof(PaymentTransaction.Status), PaymentTransactionStatus.Success);
        Set(transaction, nameof(PaymentTransaction.CreatedAtUtc), createdAtUtc);
        return transaction;
    }

    private sealed class NoIdempotencyConflict : IBookingCancellationPersistenceErrorClassifier
    {
        public bool IsIdempotencyKeyConflict(Microsoft.EntityFrameworkCore.DbUpdateException exception) => false;
    }

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);
}
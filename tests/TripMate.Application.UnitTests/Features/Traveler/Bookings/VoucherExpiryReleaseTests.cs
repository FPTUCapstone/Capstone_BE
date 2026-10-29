using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Bookings.Expiry;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.Traveler.Payments.Create;
using TripMate.Application.Features.Traveler.Payments.Status;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Bookings;

public sealed class VoucherExpiryReleaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Worker_ExpiresOnceAndReturnsVoucherSlotWithoutChangingHistory()
    {
        await using var db = TestDbContext.Create();
        var (booking, voucher) = await SeedDueBookingAsync(db);
        var processor = new PendingBookingExpiryProcessor(db, Clock(), new TourBookingOptions());

        (await processor.ExpireDueBookingsAsync(CancellationToken.None)).ExpiredCount.Should().Be(1);
        (await processor.ExpireDueBookingsAsync(CancellationToken.None)).ExpiredCount.Should().Be(0);

        voucher.UsedCount.Should().Be(0);
        booking.DiscountAmount.Should().Be(100_000m);
        (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(0);
        (await db.VoucherRedemptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TravelerScopedExpiry_ReturnsVoucherSlot()
    {
        await using var db = TestDbContext.Create();
        var (booking, voucher) = await SeedDueBookingAsync(db);
        var processor = new PendingBookingExpiryProcessor(db, Clock(), new TourBookingOptions());

        (await db.ExecuteInSerializableTransactionAsync(
            token => processor.ExpireDueBookingsForTravelerInCurrentTransactionAsync(booking.TravelerUserId, token),
            CancellationToken.None)).Should().Be(1);

        voucher.UsedCount.Should().Be(0);
        (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(0);
    }

    [Fact]
    public async Task PaymentStatusExpiry_ReturnsVoucherSlotExactlyOnce()
    {
        await using var db = TestDbContext.Create();
        var (booking, voucher) = await SeedDueBookingAsync(db);
        var handler = new GetBookingPaymentStatusQueryHandler(db,
            new FakeCurrentUserService { UserId = booking.TravelerUserId, Role = nameof(UserRole.Traveler) }, Clock());

        (await handler.Handle(new GetBookingPaymentStatusQuery(booking.Id), CancellationToken.None))
            .Value.BookingStatus.Should().Be(nameof(BookingStatus.Expired));
        (await handler.Handle(new GetBookingPaymentStatusQuery(booking.Id), CancellationToken.None))
            .Value.BookingStatus.Should().Be(nameof(BookingStatus.Expired));

        voucher.UsedCount.Should().Be(0);
        (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(0);
    }

    [Fact]
    public async Task PaymentLinkExpiry_ReturnsVoucherSlot()
    {
        await using var db = TestDbContext.Create();
        var (booking, voucher) = await SeedDueBookingAsync(db);
        var handler = new CreateOrReusePaymentLinkCommandHandler(db,
            new FakeCurrentUserService { UserId = booking.TravelerUserId, Role = nameof(UserRole.Traveler) },
            Clock(), null!, new PaymentOptions { Provider = "Mock" });

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        voucher.UsedCount.Should().Be(0);
        (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(0);
    }

    [Fact]
    public async Task PaymentCompletionExpiry_ReturnsVoucherSlotAndRetryableFailureKeepsIt()
    {
        await using var db = TestDbContext.Create();
        var (booking, voucher) = await SeedDueBookingAsync(db);
        var transaction = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock,
            booking.TotalAmount, 1234567, Now.AddMinutes(-10));
        db.PaymentTransactions.Add(transaction);
        await db.SaveChangesAsync();
        var processor = new PaymentCompletionProcessor(db, Clock());

        (await processor.CompleteAsync(transaction.Id, null, PaymentTransactionStatus.Failed,
            null, null, null, null, CancellationToken.None)).IsSuccess.Should().BeTrue();
        voucher.UsedCount.Should().Be(1);
        (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(1);

        (await processor.CompleteAsync(transaction.Id, null, PaymentTransactionStatus.Expired,
            null, null, null, null, CancellationToken.None)).IsSuccess.Should().BeTrue();
        voucher.UsedCount.Should().Be(0);
        (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(0);
        (await db.VoucherRedemptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task LatePaymentSuccess_ExpiresBookingAndReleasesVoucherSlot()
    {
        await using var db = TestDbContext.Create();
        var (booking, voucher) = await SeedDueBookingAsync(db);
        var transaction = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock,
            booking.TotalAmount, 1234568, Now.AddMinutes(-10));
        db.PaymentTransactions.Add(transaction);
        await db.SaveChangesAsync();

        var result = await new PaymentCompletionProcessor(db, Clock()).CompleteAsync(
            transaction.Id, null, PaymentTransactionStatus.Success,
            null, null, null, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        booking.Status.Should().Be(BookingStatus.Expired);
        voucher.UsedCount.Should().Be(0);
        (await db.TourSchedules.SingleAsync()).ReservedCapacity.Should().Be(0);
    }

    [Fact]
    public async Task VoucherReleaseHelper_DoesNotReleasePendingOrPaidBooking()
    {
        await using var db = TestDbContext.Create();
        var (booking, voucher) = await SeedDueBookingAsync(db);

        await BookingVoucherUsage.ReleaseAsync(db, booking, CancellationToken.None);
        voucher.UsedCount.Should().Be(1);

        booking.MarkPaid(Now.AddMinutes(-10));
        await BookingVoucherUsage.ReleaseAsync(db, booking, CancellationToken.None);
        voucher.UsedCount.Should().Be(1);
    }

    [Fact]
    public async Task ExpiryWithoutRedemption_DoesNotDecreaseVoucherUsage()
    {
        await using var db = TestDbContext.Create();
        var (booking, voucher) = await SeedDueBookingAsync(db);
        db.VoucherRedemptions.RemoveRange(db.VoucherRedemptions);
        voucher.ReleaseUsage();
        await db.SaveChangesAsync();

        var result = await new PendingBookingExpiryProcessor(db, Clock(), new TourBookingOptions())
            .ExpireDueBookingsAsync(CancellationToken.None);

        result.ExpiredCount.Should().Be(1);
        booking.Status.Should().Be(BookingStatus.Expired);
        voucher.UsedCount.Should().Be(0);
    }

    [Fact]
    public async Task Worker_WhenSaveFails_DoesNotPersistVoucherOrBookingTransition()
    {
        await using var db = TestDbContext.Create();
        await SeedDueBookingAsync(db);
        db.ThrowOnSaveConcurrency = true;

        var result = await new PendingBookingExpiryProcessor(db, Clock(), new TourBookingOptions())
            .ExpireDueBookingsAsync(CancellationToken.None);

        result.ExpiredCount.Should().Be(0);
        result.Failures.Should().ContainSingle();
        await AssertPersistedPendingSlotAsync(db);
    }

    [Fact]
    public async Task PaymentStatus_WhenSaveFails_DoesNotPersistVoucherOrBookingTransition()
    {
        await using var db = TestDbContext.Create();
        var (booking, _) = await SeedDueBookingAsync(db);
        db.ThrowOnSaveConcurrency = true;
        var handler = new GetBookingPaymentStatusQueryHandler(db,
            new FakeCurrentUserService { UserId = booking.TravelerUserId, Role = nameof(UserRole.Traveler) }, Clock());

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            handler.Handle(new GetBookingPaymentStatusQuery(booking.Id), CancellationToken.None));

        db.IsExecutingSerializableTransaction.Should().BeFalse();
        db.TransactionExecutionCount.Should().BeGreaterThan(0);
        await AssertPersistedPendingSlotAsync(db);
    }

    [Fact]
    public async Task PaymentLink_WhenSaveFails_DoesNotPersistVoucherOrBookingTransition()
    {
        await using var db = TestDbContext.Create();
        var (booking, _) = await SeedDueBookingAsync(db);
        db.ThrowOnSaveConcurrency = true;
        var handler = new CreateOrReusePaymentLinkCommandHandler(db,
            new FakeCurrentUserService { UserId = booking.TravelerUserId, Role = nameof(UserRole.Traveler) },
            Clock(), null!, new PaymentOptions { Provider = "Mock" });

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None));

        db.TransactionExecutionCount.Should().BeGreaterThan(0);
        await AssertPersistedPendingSlotAsync(db);
    }

    [Fact]
    public async Task PaymentCompletion_WhenSaveFails_DoesNotPersistVoucherOrBookingTransition()
    {
        await using var db = TestDbContext.Create();
        var (booking, _) = await SeedDueBookingAsync(db);
        var transaction = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock,
            booking.TotalAmount, 1234569, Now.AddMinutes(-10));
        db.PaymentTransactions.Add(transaction);
        await db.SaveChangesAsync();
        db.ThrowOnSaveConcurrency = true;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            new PaymentCompletionProcessor(db, Clock()).CompleteAsync(transaction.Id, null,
                PaymentTransactionStatus.Expired, null, null, null, null, CancellationToken.None));

        db.TransactionExecutionCount.Should().BeGreaterThan(0);
        await AssertPersistedPendingSlotAsync(db);
    }

    private static async Task AssertPersistedPendingSlotAsync(TestDbContext db)
    {
        db.ClearTrackedEntities();
        (await db.Bookings.AsNoTracking().SingleAsync()).Status.Should().Be(BookingStatus.PendingPayment);
        (await db.Vouchers.AsNoTracking().SingleAsync()).UsedCount.Should().Be(1);
    }

    private static FakeDateTimeProvider Clock() => new() { UtcNow = Now };

    private static async Task<(Booking Booking, Voucher Voucher)> SeedDueBookingAsync(TestDbContext db)
    {
        var traveler = await TourBookingTestData.SeedTravelerAsync(db, 1);
        var tour = TourBookingTestData.SeedTour(2, 1_000_000m);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourBookingTestData.SeedSchedule(tour, Now.AddDays(5), 10, 1);
        db.TourSchedules.Add(schedule);
        var voucher = TourBookingTestData.SeedVoucher("SAVE10", VoucherDiscountType.Percentage,
            10m, 500_000m, 0m, 5, 1, Now.AddDays(-1), Now.AddDays(2), 1, [tour]);
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();
        var booking = Booking.Create("BK-20261025-00001", traveler.Id, schedule.Id, voucher.Id,
            1_000_000m, 100_000m, 900_000m,
            [BookingParticipant.Create("Traveler", ParticipantType.Adult, null, null, null, null, null)],
            Now.AddMinutes(-20), Now.AddMinutes(-5));
        db.Bookings.Add(booking);
        db.VoucherRedemptions.Add(VoucherRedemption.Create(voucher, booking, 100_000m, Now.AddMinutes(-20)));
        await db.SaveChangesAsync();
        return (booking, voucher);
    }
}
using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.Traveler.Payments.Create;
using TripMate.Application.Features.Traveler.Payments.Mock;
using TripMate.Application.Features.Traveler.Payments.Status;
using TripMate.Application.Features.Traveler.Payments.Webhook;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Payments;

public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateOrReuse_WithEligibleBooking_CreatesPendingTransaction()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));

        var clock = new FixedClock(Now);
        var gateway = new FakePaymentGateway();
        var options = new PaymentOptions { Provider = "Mock" };
        var handler = new CreateOrReusePaymentLinkCommandHandler(
            db, new TestCurrentUserService(travelerId), clock, gateway, options);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BookingId.Should().Be(booking.Id);
        result.Value.Status.Should().Be("Pending");
        result.Value.Amount.Should().Be(booking.TotalAmount);
        result.Value.CheckoutUrl.Should().Contain("checkout/mock");

        var transaction = await db.PaymentTransactions.SingleAsync();
        transaction.BookingId.Should().Be(booking.Id);
        transaction.Status.Should().Be(PaymentTransactionStatus.Pending);
        transaction.Amount.Should().Be(booking.TotalAmount);
    }

    [Fact]
    public async Task CreateOrReuse_WithActivePendingLink_ReusesSameLinkWithoutCreatingAnotherTransaction()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));

        var clock = new FixedClock(Now);
        var gateway = new FakePaymentGateway();
        var options = new PaymentOptions { Provider = "Mock" };
        var handler = new CreateOrReusePaymentLinkCommandHandler(
            db, new TestCurrentUserService(travelerId), clock, gateway, options);

        var first = await handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None);
        var second = await handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.PaymentId.Should().Be(first.Value.PaymentId);
        second.Value.CheckoutUrl.Should().Be(first.Value.CheckoutUrl);

        (await db.PaymentTransactions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateOrReuse_WhenExpiredPendingLink_CreatesOneReplacementLink()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(30));

        var clock = new FixedClock(Now);
        var gateway = new FakePaymentGateway();
        var options = new PaymentOptions { Provider = "Mock" };
        var handler = new CreateOrReusePaymentLinkCommandHandler(
            db, new TestCurrentUserService(travelerId), clock, gateway, options);

        // First attempt creates link expiring in 5 mins
        var first = await handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        // Advance clock past the payment link expiry
        clock.CurrentTime = Now.AddMinutes(20);

        var second = await handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        second.Value.PaymentId.Should().NotBe(first.Value.PaymentId);

        var transactions = await db.PaymentTransactions.OrderBy(t => t.Id).ToListAsync();
        transactions.Should().HaveCount(2);
        transactions[0].Status.Should().Be(PaymentTransactionStatus.Expired);
        transactions[1].Status.Should().Be(PaymentTransactionStatus.Pending);
    }

    [Fact]
    public async Task CreateOrReuse_WhenBookingHoldExpired_ReturnsConflictAndMarksBookingExpired()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(-5));

        var clock = new FixedClock(Now);
        var gateway = new FakePaymentGateway();
        var options = new PaymentOptions { Provider = "Mock" };
        var handler = new CreateOrReusePaymentLinkCommandHandler(
            db, new TestCurrentUserService(travelerId), clock, gateway, options);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentBookingExpired);

        var reloaded = await db.Bookings.FindAsync(booking.Id);
        reloaded!.Status.Should().Be(BookingStatus.Expired);
    }

    [Fact]
    public async Task CreateOrReuse_WhenBookingAlreadyPaid_ReturnsConflict()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.Confirmed, PaymentStatus.Paid,
            paymentExpiresAtUtc: Now.AddMinutes(15));

        var clock = new FixedClock(Now);
        var gateway = new FakePaymentGateway();
        var options = new PaymentOptions { Provider = "Mock" };
        var handler = new CreateOrReusePaymentLinkCommandHandler(
            db, new TestCurrentUserService(travelerId), clock, gateway, options);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentBookingNotEligible);
    }

    [Fact]
    public async Task MockOutcome_Success_AtomicallyConfirmsBookingAndMarksTransactionSuccess()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));

        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9991, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var processor = new PaymentCompletionProcessor(db, clock);
        var handler = new CompleteMockPaymentCommandHandler(
            db, new TestCurrentUserService(travelerId), processor, new PaymentOptions { Provider = "Mock" });

        var result = await handler.Handle(new CompleteMockPaymentCommand(tx.Id, "Success"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Success");
        result.Value.BookingStatus.Should().Be("Confirmed");
        result.Value.PaymentStatus.Should().Be("Paid");

        var reloadedBooking = await db.Bookings.FindAsync(booking.Id);
        reloadedBooking!.Status.Should().Be(BookingStatus.Confirmed);
        reloadedBooking.PaymentStatus.Should().Be(PaymentStatus.Paid);

        var reloadedTx = await db.PaymentTransactions.FindAsync(tx.Id);
        reloadedTx!.Status.Should().Be(PaymentTransactionStatus.Success);
    }

    [Fact]
    public async Task MockOutcome_Cancelled_PreservesBookingPendingState()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));

        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9992, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var processor = new PaymentCompletionProcessor(db, clock);
        var handler = new CompleteMockPaymentCommandHandler(
            db, new TestCurrentUserService(travelerId), processor, new PaymentOptions { Provider = "Mock" });

        var result = await handler.Handle(new CompleteMockPaymentCommand(tx.Id, "Cancelled"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Cancelled");
        result.Value.BookingStatus.Should().Be("PendingPayment");

        var reloadedBooking = await db.Bookings.FindAsync(booking.Id);
        reloadedBooking!.Status.Should().Be(BookingStatus.PendingPayment);

        var reloadedTx = await db.PaymentTransactions.FindAsync(tx.Id);
        reloadedTx!.Status.Should().Be(PaymentTransactionStatus.Cancelled);
    }

    [Fact]
    public async Task MockOutcome_WhenMockProviderDisabled_ReturnsNotFound()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid);
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9993, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var processor = new PaymentCompletionProcessor(db, clock);
        var handler = new CompleteMockPaymentCommandHandler(
            db, new TestCurrentUserService(travelerId), processor, new PaymentOptions { Provider = "PayOS" });

        var result = await handler.Handle(new CompleteMockPaymentCommand(tx.Id, "Success"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentMockDisabled);
    }

    [Fact]
    public async Task GetStatus_ReturnsCanonicalBookingAndTransactionState()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));

        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9994, Now,
            checkoutUrl: "http://localhost:3001/checkout/mock?paymentId=1");
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var handler = new GetBookingPaymentStatusQueryHandler(
            db, new TestCurrentUserService(travelerId), clock);

        var result = await handler.Handle(new GetBookingPaymentStatusQuery(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BookingId.Should().Be(booking.Id);
        result.Value.BookingStatus.Should().Be("PendingPayment");
        result.Value.TransactionStatus.Should().Be("Pending");
        result.Value.CheckoutUrl.Should().Be(tx.CheckoutUrl);
    }

    [Fact]
    public async Task PayOsWebhook_LateSuccessAfterBookingExpiry_PersistsTxSuccessWithoutReinstatingBooking()
    {
        await using var db = CreateDbContext();
        var (_, booking) = await SeedBookingAsync(db, BookingStatus.Expired, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(-10));

        const long orderCode = 88881;
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, booking.TotalAmount, orderCode, Now.AddMinutes(-15));
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var processor = new PaymentCompletionProcessor(db, clock);
        var options = new PaymentOptions();
        var handler = new ProcessPayOsWebhookCommandHandler(processor, options);

        var payload = new PayOsWebhookPayload(
            Code: "00",
            Desc: "success",
            Data: new PayOsWebhookData(
                OrderCode: orderCode,
                Amount: booking.TotalAmount,
                Description: "Payment",
                AccountNumber: "123456",
                Reference: "PAYOS-REF-1",
                TransactionDateTime: Now.ToString("o"),
                Currency: "VND",
                PaymentLinkId: "LINK-1",
                Code: "00",
                Desc: "success"),
            Signature: "test-sig");

        var result = await handler.Handle(new ProcessPayOsWebhookCommand(payload), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var reloadedTx = await db.PaymentTransactions.FindAsync(tx.Id);
        reloadedTx!.Status.Should().Be(PaymentTransactionStatus.Success);

        // Decision 8: Booking remains Expired and is never reinstated
        var reloadedBooking = await db.Bookings.FindAsync(booking.Id);
        reloadedBooking!.Status.Should().Be(BookingStatus.Expired);
        reloadedBooking.PaymentStatus.Should().Be(PaymentStatus.Unpaid);
    }

    [Fact]
    public async Task PayOsWebhook_DuplicateCall_IsIdempotent()
    {
        await using var db = CreateDbContext();
        var (_, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));

        const long orderCode = 88882;
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, booking.TotalAmount, orderCode, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var processor = new PaymentCompletionProcessor(db, clock);
        var options = new PaymentOptions();
        var handler = new ProcessPayOsWebhookCommandHandler(processor, options);

        var payload = new PayOsWebhookPayload(
            Code: "00",
            Desc: "success",
            Data: new PayOsWebhookData(
                OrderCode: orderCode,
                Amount: booking.TotalAmount,
                Description: "Payment",
                AccountNumber: "123456",
                Reference: "PAYOS-REF-2",
                TransactionDateTime: Now.ToString("o"),
                Currency: "VND",
                PaymentLinkId: "LINK-2",
                Code: "00",
                Desc: "success"),
            Signature: "test-sig");

        var first = await handler.Handle(new ProcessPayOsWebhookCommand(payload), CancellationToken.None);
        var second = await handler.Handle(new ProcessPayOsWebhookCommand(payload), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();

        var reloadedTx = await db.PaymentTransactions.FindAsync(tx.Id);
        reloadedTx!.Status.Should().Be(PaymentTransactionStatus.Success);
        var reloadedBooking = await db.Bookings.FindAsync(booking.Id);
        reloadedBooking!.Status.Should().Be(BookingStatus.Confirmed);
        reloadedBooking.PaymentStatus.Should().Be(PaymentStatus.Paid);
    }

    private static TestDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static async Task<(long TravelerId, Booking Booking)> SeedBookingAsync(
        TestDbContext db,
        BookingStatus status,
        PaymentStatus paymentStatus,
        DateTimeOffset? paymentExpiresAtUtc = null)
    {
        const long travelerId = 42;
        var user = new User
        {
            Id = travelerId,
            Email = "traveler@test.local",
            FullName = "Test Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);

        var participant = BookingParticipant.Create(
            "Adult Traveler", ParticipantType.Adult, null, null, null, null, null);
        var expiresAt = paymentExpiresAtUtc ?? Now.AddMinutes(15);
        var bookedAt = expiresAt.AddMinutes(-15);
        var booking = Booking.Create(
            bookingCode: "BK-20261018-00001",
            travelerUserId: travelerId,
            tourScheduleId: 101,
            voucherId: null,
            unitPrice: 2_500_000m,
            discountAmount: 0m,
            totalAmount: 2_500_000m,
            participants: [participant],
            bookedAtUtc: bookedAt,
            paymentExpiresAtUtc: expiresAt);

        if (status == BookingStatus.Confirmed && paymentStatus == PaymentStatus.Paid)
        {
            booking.MarkPaid(Now);
        }
        else if (status == BookingStatus.Expired)
        {
            booking.ExpirePendingPayment(Now);
        }

        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        return (travelerId, booking);
    }

    private sealed class FakePaymentGateway : IPaymentGateway
    {
        public PaymentGateway Gateway => PaymentGateway.Mock;

        public Task<PaymentLinkResult> CreatePaymentLinkAsync(
            PaymentLinkRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PaymentLinkResult(
                PaymentLinkId: $"MOCK-LINK-{request.OrderCode}",
                CheckoutUrl: $"http://localhost:3001/checkout/mock?paymentId={request.OrderCode}&bookingId={request.BookingId}",
                ExpiresAtUtc: request.ExpiresAtUtc));
    }

    private sealed class FixedClock(DateTimeOffset initial) : IDateTimeProvider
    {
        public DateTimeOffset CurrentTime { get; set; } = initial;
        public DateTimeOffset UtcNow => CurrentTime;
    }

    private sealed class TestCurrentUserService(long userId) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Email => $"user-{userId}@test.local";
        public string? Role => nameof(UserRole.Traveler);
    }
}
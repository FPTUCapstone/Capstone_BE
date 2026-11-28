using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.Traveler.Payments.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Payments;

public sealed class CreateOrReusePaymentLinkHandlerTests
{
    [Fact]
    public async Task MockAttemptPersistsTheResolvedGateway()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);
        SeedReadyChannel(db);
        var result = await BuildHandler(db).Handle(new CreateOrReusePaymentLinkCommand(1), default);
        result.IsSuccess.Should().BeTrue();
        db.PaymentTransactions.Single().Gateway.Should().Be(PaymentGateway.Mock);
    }
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static CreateOrReusePaymentLinkCommandHandler BuildHandler(
        TestDbContext db,
        FakePaymentGateway? platformGateway = null,
        FakeOperatorPaymentGatewayFactory? operatorFactory = null,
        FakePaymentCredentialProtector? credProtector = null,
        FakeDateTimeProvider? clock = null,
        long travelerId = 1,
        PaymentOptions? options = null)
    {
        return new CreateOrReusePaymentLinkCommandHandler(
            db,
            new FakeCurrentUserService { UserId = travelerId, Role = nameof(UserRole.Traveler) },
            clock ?? new FakeDateTimeProvider { UtcNow = Now },
            platformGateway ?? new FakePaymentGateway(),
            credProtector ?? new FakePaymentCredentialProtector(),
            operatorFactory ?? new FakeOperatorPaymentGatewayFactory(),
            options ?? TestPaymentOptions(),
            NullLogger<CreateOrReusePaymentLinkCommandHandler>.Instance);
    }

    private static PaymentOptions TestPaymentOptions(int mockExpiryMinutes = 0) => new()
    {
        Provider = "Mock",
        PayOs = new PayOsOptions
        {
            ReturnUrl = "https://app.example.com/checkout/return",
            CancelUrl = "https://app.example.com/checkout/cancel",
        },
        Mock = new MockPaymentOptions { ExpiryMinutes = mockExpiryMinutes },
    };

    /// <summary>
    /// Seeds a minimal booking with navigation chain Booking → TourSchedule → Tour.
    /// Uses reflection to work around private setters on domain entities.
    /// </summary>
    private static Booking SeedBooking(
        TestDbContext db,
        long travelerId = 1,
        long operatorUserId = 10,
        PaymentFlow flow = PaymentFlow.PlatformCollectedLegacy,
        DateTimeOffset? paymentExpiry = null,
        long bookingId = 1,
        long scheduleId = 100,
        long tourId = 50)
    {
        var expiry = paymentExpiry ?? Now.AddHours(1);

        // Tour (private setters → reflection)
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        SetProp(tour, "Id", tourId);
        SetProp(tour, "OperatorUserId", operatorUserId);
        SetProp(tour, "Title", "Test Tour");
        SetProp(tour, "BasePrice", 50_000m);
        SetProp(tour, "TimeZoneId", "SE Asia Standard Time");
        SetProp(tour, "DestinationSummary", "Test");

        // TourSchedule
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        SetProp(schedule, "Id", scheduleId);
        SetProp(schedule, "TourId", tourId);
        SetProp(schedule, "Tour", tour);
        SetProp(schedule, "StartAtUtc", Now.AddDays(10));
        SetProp(schedule, "EndAtUtc", Now.AddDays(11));
        SetProp(schedule, "TotalCapacity", 10);
        SetProp(schedule, "MinimumCapacity", 1);
        SetProp(schedule, "ReservedCapacity", 1);
        SetProp(schedule, "Status", TourScheduleStatus.Scheduled);

        // Booking
        var booking = Booking.Restore(bookingId, travelerId, scheduleId, 1, 50_000m,
            BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            Booking.DefaultCancellationPolicySnapshot);
        SetProp(booking, "BookingCode", "BK-20261125-001");
        SetProp(booking, "PaymentExpiresAtUtc", expiry);
        SetProp(booking, "TourSchedule", schedule);

        if (flow == PaymentFlow.OperatorDirect)
            booking.StampPaymentFlow(PaymentFlow.OperatorDirect);

        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        db.Bookings.Add(booking);
        db.SaveChangesAsync(default).GetAwaiter().GetResult();
        return booking;
    }

    private static OperatorPaymentChannel SeedReadyChannel(
        TestDbContext db,
        long operatorUserId = 10,
        string clientIdRef = "ref:client-1",
        string apiKeyRef = "ref:api-1",
        string checksumRef = "ref:checksum-1")
    {
        var ch = OperatorPaymentChannel.Create(operatorUserId, Now);
        ch.SetCredentialRefs(clientIdRef, apiKeyRef, checksumRef,
            "123456789", "TO Account", "VCB", Now);
        ch.MarkReady("123456789", "TO Account", "VCB", "https://hook.example.com/to", Now);
        db.OperatorPaymentChannels.Add(ch);
        db.SaveChangesAsync(default).GetAwaiter().GetResult();
        return ch;
    }

    private static void SetProp(object obj, string name, object? value)
    {
        var type = obj.GetType();
        while (type is not null)
        {
            var prop = type.GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
            if (prop is not null) { prop.SetValue(obj, value); return; }
            type = type.BaseType;
        }
        throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType().Name}.");
    }

    // ── U1: PlatformCollectedLegacy → platform gateway ───────────────────────

    [Fact]
    public async Task U1_Platform_booking_uses_platform_gateway()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.PlatformCollectedLegacy);
        var platformGw = new FakePaymentGateway();
        var factory = new FakeOperatorPaymentGatewayFactory();
        var handler = BuildHandler(db, platformGateway: platformGw, operatorFactory: factory);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeTrue();
        platformGw.CreateCallCount.Should().Be(1);
        factory.LastSnapshot.Should().BeNull();
    }

    // ── U2: OperatorDirect → TO gateway ──────────────────────────────────────

    [Fact]
    public async Task U2_OperatorDirect_booking_uses_TO_gateway()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);
        SeedReadyChannel(db);
        var platformGw = new FakePaymentGateway();
        var toGw = new FakePaymentGateway();
        var factory = new FakeOperatorPaymentGatewayFactory { GatewayToReturn = toGw };
        var handler = BuildHandler(db, platformGateway: platformGw, operatorFactory: factory);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeTrue();
        factory.LastSnapshot.Should().NotBeNull();
        toGw.CreateCallCount.Should().Be(1);
        platformGw.CreateCallCount.Should().Be(0);
    }

    // ── U3: OperatorDirect, channel NOT FOUND ────────────────────────────────

    [Fact]
    public async Task U3_OperatorDirect_no_channel_returns_channel_not_ready()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);
        // No channel seeded
        var platformGw = new FakePaymentGateway();
        var factory = new FakeOperatorPaymentGatewayFactory();
        var handler = BuildHandler(db, platformGateway: platformGw, operatorFactory: factory);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.ChannelNotReady);
        platformGw.CreateCallCount.Should().Be(0);
        factory.LastSnapshot.Should().BeNull();
    }

    // ── U4: OperatorDirect, channel PendingVerification ──────────────────────

    [Fact]
    public async Task U4_OperatorDirect_channel_pending_verification_returns_channel_not_ready()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);

        var ch = OperatorPaymentChannel.Create(10, Now);
        ch.SetCredentialRefs("ref:c", "ref:a", "ref:k", "123", "Name", "VCB", Now);
        // Status is PendingVerification after SetCredentialRefs — do NOT call MarkReady
        db.OperatorPaymentChannels.Add(ch);
        await db.SaveChangesAsync(default);

        var handler = BuildHandler(db);
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.ChannelNotReady);
    }

    // ── U5: OperatorDirect, channel ConnectionError ───────────────────────────

    [Fact]
    public async Task U5_OperatorDirect_channel_connection_error_returns_channel_not_ready()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);

        var ch = OperatorPaymentChannel.Create(10, Now);
        ch.SetCredentialRefs("ref:c", "ref:a", "ref:k", "123", "Name", "VCB", Now);
        ch.MarkConnectionError(Now);
        db.OperatorPaymentChannels.Add(ch);
        await db.SaveChangesAsync(default);

        var handler = BuildHandler(db);
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.ChannelNotReady);
    }

    // ── U6: OperatorDirect, channel Disabled ─────────────────────────────────

    [Fact]
    public async Task U6_OperatorDirect_channel_disabled_returns_channel_not_ready()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);

        var ch = OperatorPaymentChannel.Create(10, Now);
        ch.SetCredentialRefs("ref:c", "ref:a", "ref:k", "123", "Name", "VCB", Now);
        ch.MarkReady("123", "Name", "VCB", "https://hook.example.com", Now);
        ch.Disable(Now);
        db.OperatorPaymentChannels.Add(ch);
        await db.SaveChangesAsync(default);

        var handler = BuildHandler(db);
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.ChannelNotReady);
    }

    // ── U7: OperatorDirect, existing valid link → reuse ──────────────────────

    [Fact]
    public async Task U7_OperatorDirect_existing_valid_link_is_reused()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);
        var channel = SeedReadyChannel(db);

        // Seed existing Pending transaction with CheckoutUrl
        var existingTx = PaymentTransaction.Create(1, PaymentGateway.PayOS, 50_000m, 1234567890L, Now,
            expiresAtUtc: Now.AddMinutes(14));
        existingTx.UpdateCheckoutDetails("link-id", "https://pay.example.com/existing", null, Now.AddMinutes(14));
        existingTx.BindOperatorChannel(channel.Id, channel.ChannelVersion, channel.ChecksumKeyRef!);
        db.PaymentTransactions.Add(existingTx);
        await db.SaveChangesAsync(default);

        var factory = new FakeOperatorPaymentGatewayFactory();
        var handler = BuildHandler(db, operatorFactory: factory);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.CheckoutUrl.Should().Be("https://pay.example.com/existing");
        // ResolveGatewayAsync calls the factory before the reuse check; what matters is
        // that the TO gateway's Create was never invoked.
        factory.GatewayToReturn.CreateCallCount.Should().Be(0);
    }

    // ── U8: OperatorDirect, expired pending → expire + create new ────────────

    [Fact]
    public async Task DirectAttemptWithoutOriginalKeyBindingIsNotReturnedToCustomer()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);
        SeedReadyChannel(db);
        var payment = PaymentTransaction.Create(1, PaymentGateway.PayOS, 50_000m, 1234567890L, Now,
            expiresAtUtc: Now.AddMinutes(14), checkoutUrl: "https://pay.example.com/unbound");
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();
        var result = await BuildHandler(db).Handle(new CreateOrReusePaymentLinkCommand(1), default);
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("payment.reconciliation_required");
    }

    [Fact]
    public async Task U8_OperatorDirect_expired_pending_link_creates_new()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);
        SeedReadyChannel(db);

        // Seed Pending transaction that expired 1 minute ago
        var expiredTx = PaymentTransaction.Create(1, PaymentGateway.PayOS, 50_000m, 9999999999L,
            Now.AddMinutes(-16), expiresAtUtc: Now.AddMinutes(-1));
        db.PaymentTransactions.Add(expiredTx);
        await db.SaveChangesAsync(default);

        var toGw = new FakePaymentGateway();
        var factory = new FakeOperatorPaymentGatewayFactory { GatewayToReturn = toGw };
        var handler = BuildHandler(db, operatorFactory: factory);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeTrue();
        factory.LastSnapshot.Should().NotBeNull();
        toGw.CreateCallCount.Should().Be(1);
    }

    // ── U9: OperatorDirect, gateway throws → fail ────────────────────────────

    [Fact]
    public async Task U9_OperatorDirect_gateway_throws_returns_gateway_unavailable()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);
        SeedReadyChannel(db);

        var toGw = new FakePaymentGateway { ThrowOnCreate = new HttpRequestException("Gateway error") };
        var factory = new FakeOperatorPaymentGatewayFactory { GatewayToReturn = toGw };
        var handler = BuildHandler(db, operatorFactory: factory);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentGatewayUnavailable);

        var txInDb = db.PaymentTransactions.FirstOrDefault(t => t.BookingId == 1);
        txInDb.Should().NotBeNull();
        txInDb!.Status.Should().Be(PaymentTransactionStatus.Failed);
    }

    // ── U10: OperatorDirect, persist fails after link created → cancel TO gateway

    [Fact]
    public async Task U10_OperatorDirect_persist_fail_after_link_created_cancels_TO_gateway()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect);
        SeedReadyChannel(db);

        var toGw = new FakePaymentGateway();
        var factory = new FakeOperatorPaymentGatewayFactory { GatewayToReturn = toGw };

        // Fail on the second SaveChangesAsync (first persists Pending claim, second persists CheckoutUrl)
        var saveCount = 0;
        db.SaveChangesFailureFactory = () =>
        {
            saveCount++;
            return saveCount == 2 ? new DbUpdateException("Simulated persist failure") : null;
        };

        var handler = BuildHandler(db, operatorFactory: factory);
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentGatewayUnavailable);
        toGw.CancelCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── U11: OperatorDirect expiry capped at 15 min when booking expires later ─

    [Fact]
    public async Task U11_OperatorDirect_expiry_capped_at_15_minutes()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect, paymentExpiry: Now.AddHours(2));
        SeedReadyChannel(db);

        var handler = BuildHandler(db);
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeTrue();
        var tx = db.PaymentTransactions.First(t => t.BookingId == 1);
        tx.ExpiresAtUtc.Should().NotBeNull();
        tx.ExpiresAtUtc!.Value.Should().BeOnOrBefore(Now.AddMinutes(15).AddSeconds(2));
    }

    // ── U12: OperatorDirect expiry = bookingExpiry when it's earlier than 15 min

    [Fact]
    public async Task U12_OperatorDirect_uses_booking_expiry_when_earlier_than_15_min()
    {
        var shortExpiry = Now.AddMinutes(5);
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect, paymentExpiry: shortExpiry);
        SeedReadyChannel(db);

        var handler = BuildHandler(db);
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeTrue();
        var tx = db.PaymentTransactions.First(t => t.BookingId == 1);
        tx.ExpiresAtUtc.Should().Be(shortExpiry);
    }

    // ── U13: PlatformCollectedLegacy, Mock.ExpiryMinutes = 30 → now+30min ─────

    [Fact]
    public async Task U13_Platform_mock_expiry_minutes_applied()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.PlatformCollectedLegacy, paymentExpiry: Now.AddHours(2));

        var handler = BuildHandler(db, options: TestPaymentOptions(mockExpiryMinutes: 30));
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeTrue();
        var tx = db.PaymentTransactions.First(t => t.BookingId == 1);
        tx.ExpiresAtUtc.Should().NotBeNull();
        tx.ExpiresAtUtc!.Value.Should().BeOnOrBefore(Now.AddMinutes(30).AddSeconds(2));
    }

    // ── U14: OperatorDirect ignores Mock.ExpiryMinutes ───────────────────────

    [Fact]
    public async Task U14_OperatorDirect_ignores_mock_expiry_minutes()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.OperatorDirect, paymentExpiry: Now.AddHours(3));
        SeedReadyChannel(db);

        var handler = BuildHandler(db, options: TestPaymentOptions(mockExpiryMinutes: 60));
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeTrue();
        var tx = db.PaymentTransactions.First(t => t.BookingId == 1);
        tx.ExpiresAtUtc.Should().NotBeNull();
        tx.ExpiresAtUtc!.Value.Should().BeOnOrBefore(Now.AddMinutes(15).AddSeconds(2));
    }

    // ── U15: Different traveler → not_found ───────────────────────────────────

    [Fact]
    public async Task U15_Different_traveler_cannot_access_booking()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, travelerId: 1, flow: PaymentFlow.PlatformCollectedLegacy);

        // Handler runs as traveler 2
        var handler = BuildHandler(db, travelerId: 2);
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentNotFound);
    }

    // ── U16: Booking status != PendingPayment → not_eligible ─────────────────

    [Fact]
    public async Task U16_Cancelled_booking_not_eligible()
    {
        using var db = TestDbContext.Create();
        var booking = SeedBooking(db, flow: PaymentFlow.PlatformCollectedLegacy);
        // Override status to Cancelled via reflection
        SetProp(booking, "Status", BookingStatus.Cancelled);
        db.Bookings.Update(booking);
        await db.SaveChangesAsync(default);

        var handler = BuildHandler(db);
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentBookingNotEligible);
    }

    // ── U17: Booking PaymentExpiresAtUtc passed → expired ────────────────────

    [Fact]
    public async Task U17_Booking_with_expired_payment_window_returns_booking_expired()
    {
        using var db = TestDbContext.Create();
        SeedBooking(db, flow: PaymentFlow.PlatformCollectedLegacy, paymentExpiry: Now.AddMinutes(-5));

        var handler = BuildHandler(db);
        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(1), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentBookingExpired);
    }
}
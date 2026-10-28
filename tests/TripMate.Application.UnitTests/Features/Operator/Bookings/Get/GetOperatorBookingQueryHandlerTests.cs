using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Bookings.Common;
using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Application.Features.Operator.Bookings.Get;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorBookingTestFixtures;
using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Bookings.Get;

public sealed class GetOperatorBookingQueryHandlerTests
{
    private const long OperatorA = 7;
    private const long OperatorB = 8;

    private static GetOperatorBookingQueryHandler CreateHandler(TestDbContext db, long? userId, string? role) =>
        new(db, new FakeCurrentUserService { UserId = userId, Role = role });

    private static GetOperatorBookingQueryHandler OperatorHandler(TestDbContext db, long operatorId = OperatorA) =>
        CreateHandler(db, operatorId, nameof(UserRole.TourOperator));

    /// <summary>Operator A owns tour 101 / schedule 201; operator B owns tour 102 / schedule 202.</summary>
    private static async Task SeedTwoOperatorsAsync(TestDbContext db)
    {
        await SeedApprovedOperatorAsync(db, OperatorA);
        await SeedApprovedOperatorAsync(db, OperatorB);
        await SeedTourWithScheduleAsync(db, OperatorA, 101, 201, "Tour A", meetingPoint: "Da Nang Cruise Port");
        await SeedTourWithScheduleAsync(db, OperatorB, 102, 202, "Tour B");
        await SeedBookerAsync(db, 50, "Alice Nguyen", "alice@example.com", "+84901234567");
        await SeedBookerAsync(db, 51, "Bob Tran", "bob@example.com");
    }

    private static async Task<PaymentTransaction> SeedPaymentAsync(
        TestDbContext db, long bookingId, decimal amount, PaymentGateway gateway = PaymentGateway.PayOS,
        PaymentTransactionStatus status = PaymentTransactionStatus.Success, string? gatewayRef = "FT26102012345",
        DateTimeOffset? createdAtUtc = null, long? orderCode = null)
    {
        var created = createdAtUtc ?? new DateTimeOffset(2026, 10, 20, 3, 5, 0, TimeSpan.Zero);
        var transaction = PaymentTransaction.Create(bookingId, gateway, amount, orderCode ?? bookingId * 100 + 1, created,
            checkoutUrl: "https://pay.example/checkout/secret", paymentLinkId: "plink-secret", qrCode: "qr-secret");
        if (status == PaymentTransactionStatus.Success)
        {
            transaction.MarkSuccess(gatewayRef, "{\"raw\":\"gateway-secret\"}", created.AddSeconds(40), 10m);
        }
        else if (status == PaymentTransactionStatus.Failed)
        {
            transaction.MarkFailed("{\"raw\":\"gateway-secret\"}", created.AddSeconds(40));
        }

        db.PaymentTransactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction;
    }

    // ---- authorization, ownership ----

    [Fact]
    public async Task Handle_NonOperatorOrUnapproved_IsDenied()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50);

        var traveler = await CreateHandler(db, 50, nameof(UserRole.Traveler))
            .Handle(new GetOperatorBookingQuery(1), CancellationToken.None);
        Set(db.OperatorProfiles.Single(profile => profile.UserId == OperatorA),
            nameof(OperatorProfile.ApprovalStatus), OperatorApprovalStatus.Rejected);
        await db.SaveChangesAsync();
        var unapproved = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);

        traveler.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
        unapproved.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
    }

    [Fact]
    public async Task Handle_BookingOnAnotherOperatorsTour_IsForbidden()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 2, 202, 51);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(2), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorBookingErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_UnknownBooking_IsNotFound()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(999), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorBookingErrorCodes.NotFound);
    }

    // ---- mapping ----

    [Fact]
    public async Task Handle_MapsTourScheduleBookerAndAmounts()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        var bookedAt = new DateTimeOffset(2026, 10, 20, 3, 15, 0, TimeSpan.Zero);
        await SeedBookingAsync(db, 1, 201, 50, bookingCode: "BK-20261020-0007", quantity: 3,
            totalAmount: 2_400_000m, discountAmount: 100_000m, bookedAtUtc: bookedAt);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.BookingId.Should().Be("1");
        detail.BookingCode.Should().Be("BK-20261020-0007");
        detail.Status.Should().Be(BookingStatus.Confirmed);
        detail.PaymentStatus.Should().Be(PaymentStatus.Paid);
        detail.BookedAtUtc.Should().Be(bookedAt);
        detail.CancelledAtUtc.Should().BeNull();
        detail.CancelReason.Should().BeNull();
        detail.Tour.Should().Be(new TripMate.Application.Features.Operator.Bookings.List.OperatorBookingTourDto("101", "Tour A"));
        detail.Schedule.ScheduleId.Should().Be("201");
        detail.Schedule.StartAtUtc.Should().Be(DefaultStartUtc);
        detail.Schedule.EndAtUtc.Should().Be(DefaultStartUtc.AddHours(10));
        detail.Schedule.MeetingPoint.Should().Be("Da Nang Cruise Port");
        detail.Booker.Should().Be(new OperatorBookingDetailBookerDto("50", "Alice Nguyen", "alice@example.com", "+84901234567"));
        detail.Quantity.Should().Be(3);
        detail.SubtotalAmount.Should().Be(2_500_000m);
        detail.DiscountAmount.Should().Be(100_000m);
        detail.TotalAmount.Should().Be(2_400_000m);
    }

    [Fact]
    public async Task Handle_Participants_ExposeOnlyNameAndType()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, participants:
        [
            BookingParticipant.Create("Nguyen Van A", ParticipantType.Adult, "+84900000001", "a@secret.example",
                BookingIdDocumentType.Passport, "protected:P1234567:cipher", "VNM"),
            BookingParticipant.Create("Nguyen Thi B", ParticipantType.Child, null, null, null, null, null),
        ]);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);

        result.Value.Participants.Should().Equal(
            new OperatorBookingParticipantDto("Nguyen Van A", ParticipantType.Adult),
            new OperatorBookingParticipantDto("Nguyen Thi B", ParticipantType.Child));
    }

    [Fact]
    public async Task Handle_NoTicket_ReturnsNullTicketAndNotCheckedIn()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);

        result.Value.Ticket.Should().BeNull();
        result.Value.CheckInStatus.Should().Be(BookingCheckIn.NotCheckedIn);
        result.Value.CheckedInAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Handle_SeveralTickets_ReturnsLatestIssuedAndDerivesCheckInFromAll()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50);
        var checkedIn = new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero);
        await SeedTicketAsync(db, 11, 1, TicketStatus.Used, checkedInAtUtc: checkedIn,
            issuedAtUtc: new DateTimeOffset(2026, 10, 20, 3, 1, 0, TimeSpan.Zero));
        await SeedTicketAsync(db, 12, 1, TicketStatus.Valid,
            issuedAtUtc: new DateTimeOffset(2026, 10, 21, 3, 1, 0, TimeSpan.Zero));

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);

        result.Value.Ticket.Should().NotBeNull();
        result.Value.Ticket!.TicketCode.Should().Be("TK-0000000012");
        result.Value.Ticket.Status.Should().Be(TicketStatus.Valid);
        result.Value.CheckInStatus.Should().Be(BookingCheckIn.CheckedIn);
        result.Value.CheckedInAtUtc.Should().Be(checkedIn);
    }

    [Fact]
    public async Task Handle_Payment_IsLatestSuccessfulPaymentIgnoringPendingFailedAndRefunds()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, totalAmount: 500_000m);
        var early = new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);
        await SeedPaymentAsync(db, 1, 500_000m, PaymentGateway.VNPAY, PaymentTransactionStatus.Success, "OLD-REF",
            early, orderCode: 101);
        await SeedPaymentAsync(db, 1, 500_000m, PaymentGateway.PayOS, PaymentTransactionStatus.Success, "NEW-REF",
            early.AddMinutes(10), orderCode: 102);
        await SeedPaymentAsync(db, 1, 500_000m, PaymentGateway.MoMo, PaymentTransactionStatus.Pending, null,
            early.AddMinutes(20), orderCode: 103);
        await SeedPaymentAsync(db, 1, 500_000m, PaymentGateway.Stripe, PaymentTransactionStatus.Failed, null,
            early.AddMinutes(30), orderCode: 104);
        db.PaymentTransactions.Add(PaymentTransaction.CreateRefund(1, 9, PaymentGateway.PayOS, 100_000m,
            early.AddMinutes(40)));
        await db.SaveChangesAsync();

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);

        var payment = result.Value.Payment;
        payment.Should().NotBeNull();
        payment!.Gateway.Should().Be(PaymentGateway.PayOS);
        payment.GatewayTransactionRef.Should().Be("NEW-REF");
        payment.Status.Should().Be(PaymentTransactionStatus.Success);
        payment.Amount.Should().Be(500_000m);
        payment.SucceededAtUtc.Should().NotBeNull();
        result.Value.PaidAmount.Should().Be(500_000m);
    }

    [Fact]
    public async Task Handle_NoVerifiedPayment_HasNullPaymentAndZeroPaidAmount()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, status: BookingStatus.PendingPayment, paymentStatus: PaymentStatus.Unpaid);
        await SeedPaymentAsync(db, 1, 500_000m, status: PaymentTransactionStatus.Pending);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);

        result.Value.Payment.Should().BeNull();
        result.Value.PaidAmount.Should().Be(0m);
    }

    [Fact]
    public async Task Handle_PaymentAmountDifferentFromTotal_IsStillShownAndPaidAmountFollowsIt()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, totalAmount: 500_000m);
        await SeedPaymentAsync(db, 1, 450_000m);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);

        result.Value.Payment!.Amount.Should().Be(450_000m);
        result.Value.PaidAmount.Should().Be(450_000m);
        result.Value.TotalAmount.Should().Be(500_000m);
    }

    [Fact]
    public async Task Handle_Coupon_IsMappedWhenAVoucherWasApplied()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var voucher = Voucher.Create(OperatorA, "SUMMER10", "Summer 10%", VoucherDiscountType.Percentage, 10m,
            100_000m, 0m, 100, 1, now, now.AddDays(60), [db.Tours.Single(tour => tour.Id == 101)], now);
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();
        await SeedBookingAsync(db, 1, 201, 50, totalAmount: 450_000m, discountAmount: 50_000m, voucherId: voucher.Id);
        await SeedBookingAsync(db, 2, 201, 50);

        var withCoupon = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);
        var without = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(2), CancellationToken.None);

        withCoupon.Value.Coupon.Should().Be(new OperatorBookingCouponDto("SUMMER10", "Summer 10%", 50_000m));
        without.Value.Coupon.Should().BeNull();
    }

    // ---- cancel eligibility (shared with UC-41) ----

    [Theory]
    [InlineData(BookingStatus.PendingPayment, PaymentStatus.Unpaid, false, true, null)]
    [InlineData(BookingStatus.Confirmed, PaymentStatus.Paid, false, true, null)]
    [InlineData(BookingStatus.Confirmed, PaymentStatus.Paid, true, false, BookingCancelEligibility.BlockedCheckedIn)]
    [InlineData(BookingStatus.Cancelled, PaymentStatus.Refunded, false, false, BookingCancelEligibility.BlockedInvalidStatus)]
    [InlineData(BookingStatus.Completed, PaymentStatus.Paid, true, false, BookingCancelEligibility.BlockedInvalidStatus)]
    [InlineData(BookingStatus.Expired, PaymentStatus.Unpaid, false, false, BookingCancelEligibility.BlockedInvalidStatus)]
    public async Task Handle_CanCancel_FollowsTheSharedUc41Rule(
        BookingStatus status, PaymentStatus paymentStatus, bool usedTicket, bool expectedCanCancel, string? expectedReason)
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, status: status, paymentStatus: paymentStatus);
        if (usedTicket)
        {
            await SeedTicketAsync(db, 11, 1, TicketStatus.Used, checkedInAtUtc: DefaultStartUtc);
        }

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);

        result.Value.CanCancel.Should().Be(expectedCanCancel);
        result.Value.CancelBlockedReason.Should().Be(expectedReason);
    }

    // ---- no sensitive data in the serialized response ----

    [Fact]
    public async Task Handle_SerializedResponse_NeverContainsSensitiveFields()
    {
        await using var db = TestDbContext.Create();
        await SeedTwoOperatorsAsync(db);
        await SeedBookingAsync(db, 1, 201, 50, participants:
        [
            BookingParticipant.Create("Nguyen Van A", ParticipantType.Adult, "+84900000001", "a@secret.example",
                BookingIdDocumentType.Passport, "protected:P1234567:cipher", "VNM"),
        ]);
        await SeedTicketAsync(db, 11, 1);
        await SeedPaymentAsync(db, 1, 500_000m);

        var result = await OperatorHandler(db).Handle(new GetOperatorBookingQuery(1), CancellationToken.None);
        string json = JsonSerializer.Serialize(result.Value);

        string[] forbidden =
        [
            "idDocument", "IdDocument", "nationality", "Nationality", "P1234567", "protected:", "cipher",
            "+84900000001", "a@secret.example", "VNM", "rawResponse", "RawResponse", "gateway-secret",
            "checkoutUrl", "CheckoutUrl", "checkout/secret", "qrCode", "QrCode", "qr-secret",
            "paymentLinkId", "PaymentLinkId", "plink-secret", "orderCode", "OrderCode",
        ];
        foreach (string value in forbidden)
        {
            json.Should().NotContain(value);
        }
    }
}
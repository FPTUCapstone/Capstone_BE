using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Navigation.Start;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.Traveler.Payments.Create;
using TripMate.Application.Features.Traveler.Payments.Mock;
using TripMate.Application.Features.Traveler.Payments.Status;
using TripMate.Application.Features.Traveler.Payments.Webhook;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Payments;

public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VerifyPayOsSignature_AcceptsOfficialWebhookPayload()
    {
        const string checksumKey = "1a54716c8f0efb2744fb28b6e38b25da7f67a925d98bc1c18bd8faaecadd7675";
        const string signature = "412e915d2871504ed31be63c8f62a149a4410d34c4c42affc9006ef9917eaa03";
        const string payload = """
            {
              "orderCode": 123,
              "amount": 3000,
              "description": "VQRIO123",
              "accountNumber": "12345678",
              "reference": "TF230204212323",
              "transactionDateTime": "2023-02-04 18:25:00",
              "currency": "VND",
              "paymentLinkId": "124c33293c43417ab7879e14c8d9eb18",
              "code": "00",
              "desc": "Thành công",
              "counterAccountBankId": "",
              "counterAccountBankName": "",
              "counterAccountName": "",
              "counterAccountNumber": "",
              "virtualAccountName": "",
              "virtualAccountNumber": ""
            }
            """;

        var data = JsonSerializer.Deserialize<PayOsWebhookData>(payload);

        data.Should().NotBeNull();
        ProcessPayOsWebhookCommandHandler.VerifyPayOsSignature(data!, signature, checksumKey)
            .Should().BeTrue();
    }

    [Fact]
    public async Task CreateOrReuse_WithEligibleBooking_CreatesPendingTransaction()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));

        var clock = new FixedClock(Now);
        var gateway = new FakePaymentGateway
        {
            OnCreate = () =>
            {
                var pendingClaim = db.PaymentTransactions.Single();
                pendingClaim.Status.Should().Be(PaymentTransactionStatus.Pending);
                pendingClaim.CheckoutUrl.Should().BeNull();
            },
        };
        var options = new PaymentOptions { Provider = "Mock" };
        var handler = new CreateOrReusePaymentLinkCommandHandler(
            db, new TestCurrentUserService(travelerId), clock, gateway, options);

        var result = await handler.Handle(new CreateOrReusePaymentLinkCommand(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BookingId.Should().Be(booking.Id);
        result.Value.Status.Should().Be("Pending");
        result.Value.Amount.Should().Be(booking.TotalAmount);
        result.Value.CheckoutUrl.Should().Contain("checkout/mock");
        gateway.LastRequest.Should().NotBeNull();
        gateway.LastRequest!.Description.Should().Be($"TM {booking.BookingCode}");
        gateway.LastRequest.Description.Length.Should().BeLessThanOrEqualTo(25);
        gateway.LastRequest.ReturnUrl.Should().Be($"http://localhost:3001/checkout/result?bookingId={booking.Id}");
        gateway.LastRequest.CancelUrl.Should().Be($"http://localhost:3001/checkout/result?bookingId={booking.Id}");

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
    public async Task MockOutcome_Success_MaterializesNavigableItineraryAndStartsNavigation()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        // Departure is already running (start = Now) so the navigation window is open.
        SeedBookableTourWithStops(db, booking.TourScheduleId, departureStartUtc: Now);
        await db.SaveChangesAsync();

        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9995, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var processor = new PaymentCompletionProcessor(db, clock);
        var handler = new CompleteMockPaymentCommandHandler(
            db, new TestCurrentUserService(travelerId), processor, new PaymentOptions { Provider = "Mock" });

        var result = await handler.Handle(new CompleteMockPaymentCommand(tx.Id, "Success"), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        var itinerary = await db.Itineraries
            .Include(entity => entity.Items)
            .SingleAsync(entity => entity.SourceBookingId == booking.Id);
        itinerary.SourceType.Should().Be(Itinerary.BookedTourSourceType);
        itinerary.Status.Should().Be(Itinerary.ActiveStatus);
        itinerary.TravelerUserId.Should().Be(travelerId);
        itinerary.ValidFromUtc.Should().Be(Now);
        itinerary.ValidToUtc.Should().Be(Now.AddHours(8));

        // Asia/Ho_Chi_Minh (+07, no DST): local departure date 2026-10-18.
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToList();
        items.Should().HaveCount(2);
        items[0].SequenceNo.Should().Be(1);
        items[0].PointOfInterestId.Should().Be(501);
        items[0].PlannedArrivalUtc.Should().Be(new DateTimeOffset(2026, 10, 18, 10, 30, 0, TimeSpan.Zero));
        items[0].PlannedDepartureUtc.Should().Be(new DateTimeOffset(2026, 10, 18, 11, 30, 0, TimeSpan.Zero));
        items[0].IsMandatory.Should().BeTrue();
        items[0].RecommendationReason.Should().Be("Golden Bridge walk");
        items[1].SequenceNo.Should().Be(2);
        items[1].PointOfInterestId.Should().Be(502);
        items[1].PlannedArrivalUtc.Should().Be(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        items[1].PlannedDepartureUtc.Should().Be(new DateTimeOffset(2026, 10, 18, 12, 45, 0, TimeSpan.Zero));

        // The snapshot produced by payment must itself be navigable via the production handler.
        var navigation = await new StartNavigationSessionCommandHandler(db, new ItineraryAccessService(db), clock)
            .Handle(new StartNavigationSessionCommand(itinerary.Id, travelerId, Guid.NewGuid()),
                CancellationToken.None);
        navigation.IsSuccess.Should().BeTrue(
            "payment output must be navigable, not an empty shell: {0}", navigation.ErrorMessage);
        navigation.Value.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task MockOutcome_Success_WithUnmaterializableTemplate_KeepsPaymentTruthAndAudits()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        // Legacy/inconsistent template: a published departure without scheduled stops.
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 11L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 7L);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        var schedule = TourSchedule.Create(Now.AddDays(4), Now.AddDays(4).AddHours(8), "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, booking.TourScheduleId);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);

        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9996, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var processor = new PaymentCompletionProcessor(db, clock);

        var result = await processor.CompleteAsync(tx.Id, null, PaymentTransactionStatus.Success,
            "gateway-ref-1", "{}", null, null, CancellationToken.None);

        // Gateway acknowledgement and payment truth must survive untouched.
        result.IsSuccess.Should().BeTrue();
        var reloadedBooking = await db.Bookings.FindAsync(booking.Id);
        reloadedBooking!.Status.Should().Be(BookingStatus.Confirmed);
        reloadedBooking.PaymentStatus.Should().Be(PaymentStatus.Paid);
        // No misleading empty Active itinerary, no invented times.
        db.Itineraries.Should().BeEmpty();
        db.ItineraryItems.Should().BeEmpty();
        db.AuditLogs.Should().Contain(audit =>
            audit.ActionType == AuditActionTypes.BookingItineraryMaterializationFailed
            && audit.Result == AuditOutcome.Failure
            && audit.Reason!.Contains(BookedTourItineraryMaterializer.NoStopsFailureCode));
    }

    [Fact]
    public async Task RawDeadlock_StopsAtMaxAttemptsAndPropagates()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, bookingId, txId) = await SeedPendingPaymentAsync(dbName, bookable: true);
        // Bare deadlock victim (SQL 1205 can surface unwrapped from reads/saves) — the
        // coordinator must retry it through the same bounded loop, then rethrow untouched.
        var faulty = FaultyContext(dbName, attempt =>
            attempt <= 3 ? new SimulatedDeadlockException() : null);

        var thrown = await Assert.ThrowsAnyAsync<SimulatedDeadlockException>(() =>
            new PaymentCompletionProcessor(faulty, new FixedClock(Now))
                .CompleteAsync(txId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None));

        thrown.Should().BeOfType<SimulatedDeadlockException>();
        faulty.SaveChangesAsyncCallCount.Should().Be(3, "bounded to three attempts, the last one rethrown");
        await using var verify = TestDbContext.Create(dbName);
        verify.Itineraries.Should().BeEmpty();
        (await verify.Bookings.FindAsync(bookingId))!.Status.Should().Be(BookingStatus.PendingPayment);
        (await verify.PaymentTransactions.FindAsync(txId))!.Status.Should().Be(PaymentTransactionStatus.Pending);
    }

    [Fact]
    public async Task RawDeadlock_CancellationIsHonoredBetweenAttempts()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, bookingId, txId) = await SeedPendingPaymentAsync(dbName, bookable: true);
        using var cancellationSource = new CancellationTokenSource();
        var faulty = FaultyContext(dbName, attempt =>
        {
            if (attempt == 1)
            {
                cancellationSource.Cancel();
                return new SimulatedDeadlockException();
            }

            return null;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PaymentCompletionProcessor(faulty, new FixedClock(Now))
                .CompleteAsync(txId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, cancellationSource.Token));

        faulty.SaveChangesAsyncCallCount.Should().Be(1, "cancellation stops the loop before attempt two");
        await using var verify = TestDbContext.Create(dbName);
        (await verify.Bookings.FindAsync(bookingId))!.Status.Should().Be(BookingStatus.PendingPayment);
        verify.Itineraries.Should().BeEmpty();
    }

    [Fact]
    public async Task SourceBookingUniqueConflict_ReloadsWinnerAndReturnsReplay()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, bookingId, txId) = await SeedPendingPaymentAsync(dbName, bookable: true);
        var faulty = FaultyContext(dbName, attempt => attempt == 1
            ? new DbUpdateException("unique conflict", new SimulatedSourceBookingUniqueConflictException())
            : null);

        var result = await new PaymentCompletionProcessor(faulty, new FixedClock(Now))
            .CompleteAsync(txId, null, PaymentTransactionStatus.Success,
                "gateway-ref-1", "{}", null, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        faulty.SaveChangesAsyncCallCount.Should().Be(2, "one conflict attempt plus the successful replay attempt");
        await using var verify = TestDbContext.Create(dbName);
        (await verify.Itineraries.CountAsync(i => i.SourceBookingId == bookingId)).Should().Be(1);
        (await verify.Bookings.FindAsync(bookingId))!.Status.Should().Be(BookingStatus.Confirmed);
    }

    [Fact]
    public async Task RepeatedSourceBookingConflict_StopsAtMaxAttemptsAndPropagates()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, bookingId, txId) = await SeedPendingPaymentAsync(dbName, bookable: true);
        var faulty = FaultyContext(dbName, attempt =>
            new DbUpdateException("unique conflict", new SimulatedSourceBookingUniqueConflictException()));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            new PaymentCompletionProcessor(faulty, new FixedClock(Now))
                .CompleteAsync(txId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None));

        faulty.SaveChangesAsyncCallCount.Should().Be(3, "bounded to three attempts, the last one rethrown");
        await using var verify = TestDbContext.Create(dbName);
        verify.Itineraries.Should().BeEmpty();
    }

    [Fact]
    public async Task RepeatedDeadlock_StopsAtMaxAttemptsAndPropagates()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, _, txId) = await SeedPendingPaymentAsync(dbName, bookable: true);
        // A fourth save would succeed (sentinel): the bounded loop must never reach it.
        var faulty = FaultyContext(dbName, attempt => attempt <= 3
            ? new DbUpdateException("deadlock victim", new SimulatedDeadlockException())
            : null);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            new PaymentCompletionProcessor(faulty, new FixedClock(Now))
                .CompleteAsync(txId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None));

        faulty.SaveChangesAsyncCallCount.Should().Be(3, "deadlock retries are bounded and never unbounded");
    }

    [Fact]
    public async Task RepeatedDeadlock_CancellationIsHonoredBetweenAttempts()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, _, txId) = await SeedPendingPaymentAsync(dbName, bookable: true);
        using var cancellationSource = new CancellationTokenSource();
        var faulty = FaultyContext(dbName, attempt =>
        {
            if (attempt == 1)
            {
                // Cancel while the first attempt fails: the retry loop must observe the
                // token before starting attempt two.
                cancellationSource.Cancel();
                return new DbUpdateException("deadlock victim", new SimulatedDeadlockException());
            }

            return null;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PaymentCompletionProcessor(faulty, new FixedClock(Now))
                .CompleteAsync(txId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, cancellationSource.Token));

        faulty.SaveChangesAsyncCallCount.Should().Be(1, "cancellation must stop the retry loop");
    }

    [Fact]
    public async Task NonUniquePersistenceFailure_IsNotRetriedAndPropagates()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, _, txId) = await SeedPendingPaymentAsync(dbName, bookable: true);
        var faulty = FaultyContext(dbName, attempt => new DbUpdateException(
            "The INSERT statement conflicted with the FOREIGN KEY constraint.",
            new InvalidOperationException("FK violation on another table.")));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            new PaymentCompletionProcessor(faulty, new FixedClock(Now))
                .CompleteAsync(txId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None));

        faulty.SaveChangesAsyncCallCount.Should().Be(1, "only source-booking unique conflicts may retry");
        await using var verify = TestDbContext.Create(dbName);
        verify.Itineraries.Should().BeEmpty();
    }

    [Fact]
    public async Task ItineraryConstraintFailure_IsNotRetriedAndPropagates()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, bookingId, txId) = await SeedPendingPaymentAsync(dbName, bookable: true);
        // The itinerary insert would be part of this save; a constraint failure on it is a
        // data error, never a concurrency conflict, so it must propagate untouched.
        var faulty = FaultyContext(dbName, attempt => new DbUpdateException(
            "The UPDATE statement conflicted with the CHECK constraint.",
            new InvalidOperationException("Check constraint violation while persisting the itinerary.")));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            new PaymentCompletionProcessor(faulty, new FixedClock(Now))
                .CompleteAsync(txId, null, PaymentTransactionStatus.Success,
                    "gateway-ref-1", "{}", null, null, CancellationToken.None));

        faulty.SaveChangesAsyncCallCount.Should().Be(1);
        await using var verify = TestDbContext.Create(dbName);
        verify.Itineraries.Should().BeEmpty();
        (await verify.Bookings.FindAsync(bookingId))!.Status.Should().Be(BookingStatus.PendingPayment);
    }

    [Fact]
    public async Task MaterializationFailure_AuditActorIsNull()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        // Legacy/inconsistent template: a published departure without scheduled stops.
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 11L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 7L);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        var schedule = TourSchedule.Create(Now.AddDays(4), Now.AddDays(4).AddHours(8), "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, booking.TourScheduleId);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9998, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var result = await new PaymentCompletionProcessor(db, new FixedClock(Now))
            .CompleteAsync(tx.Id, null, PaymentTransactionStatus.Success,
                "gateway-ref-secret", "raw-payload", null, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var audit = await db.AuditLogs
            .SingleAsync(entry => entry.ActionType == AuditActionTypes.BookingItineraryMaterializationFailed);
        audit.ActorUserId.Should().BeNull("the system workflow performed the failure, not the traveler");
        audit.AffectedEntity.Should().Be(AuditEntityTypes.Booking);
        audit.AffectedEntityId.Should().Be(booking.Id);
        audit.Result.Should().Be(AuditOutcome.Failure);
        audit.Reason.Should().Contain(BookedTourItineraryMaterializer.NoStopsFailureCode);
        audit.Reason.Should().NotContain("gateway-ref-secret");
        audit.Reason.Should().NotContain("raw-payload");
        audit.Reason.Should().NotContain(" at ");
    }

    [Fact]
    public async Task MaterializationFailure_WhenTheDepartureIsMissing_IsNotRecordedAsNoStops()
    {
        await using var db = CreateDbContext();
        var (_, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9997, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var result = await new PaymentCompletionProcessor(db, new FixedClock(Now))
            .CompleteAsync(tx.Id, null, PaymentTransactionStatus.Success,
                "gateway-ref", "{}", null, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var audit = await db.AuditLogs
            .SingleAsync(entry => entry.ActionType == AuditActionTypes.BookingItineraryMaterializationFailed);
        audit.Reason.Should().Be(
            $"Booked-tour itinerary materialization failed: {BookedTourItineraryMaterializer.DepartureNotFoundFailureCode}.");
        audit.Reason.Should().NotContain(BookedTourItineraryMaterializer.NoStopsFailureCode);
    }

    private static async Task<(long TravelerId, long BookingId, long TransactionId)> SeedPendingPaymentAsync(
        string databaseName, bool bookable)
    {
        await using var db = TestDbContext.Create(databaseName);
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        if (bookable)
        {
            SeedBookableTourWithStops(db, booking.TourScheduleId, departureStartUtc: Now);
        }

        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9999, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();
        return (travelerId, booking.Id, tx.Id);
    }

    private static TestDbContext FaultyContext(string databaseName, Func<int, Exception?> failureFactory) =>
        new(new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options)
        {
            SaveChangesAttemptFailureFactory = failureFactory,
        };

    [Fact]
    public async Task MockOutcome_Success_CreatesBookedTourItineraryLinkedToBooking()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        SeedBookableTourWithStops(db, booking.TourScheduleId, departureStartUtc: Now);
        await db.SaveChangesAsync();

        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9993, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var processor = new PaymentCompletionProcessor(db, clock);
        var handler = new CompleteMockPaymentCommandHandler(
            db, new TestCurrentUserService(travelerId), processor, new PaymentOptions { Provider = "Mock" });

        var result = await handler.Handle(new CompleteMockPaymentCommand(tx.Id, "Success"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var itineraries = await db.Itineraries.ToListAsync();
        itineraries.Should().ContainSingle();
        var itinerary = itineraries.Single();
        itinerary.SourceBookingId.Should().Be(booking.Id);
        itinerary.SourceType.Should().Be(Itinerary.BookedTourSourceType);
        itinerary.TravelerUserId.Should().Be(travelerId);
        itinerary.Status.Should().Be(Itinerary.ActiveStatus);
        itinerary.SourceTourId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MockOutcome_ReplayDoesNotDuplicateBookedTourItinerary()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        SeedBookableTourWithStops(db, booking.TourScheduleId, departureStartUtc: Now);

        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9994, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var processor = new PaymentCompletionProcessor(db, clock);

        (await processor.CompleteAsync(tx.Id, null, PaymentTransactionStatus.Success,
            "ref-1", "{}", null, null, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await processor.CompleteAsync(tx.Id, null, PaymentTransactionStatus.Success,
            "ref-1", "{}", null, null, CancellationToken.None)).IsSuccess.Should().BeTrue();

        db.Itineraries.Should().ContainSingle(itinerary => itinerary.SourceBookingId == booking.Id);
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
    public async Task PayOsWebhook_LateSuccessAfterBookingExpiry_DoesNotReopenTerminalTransactionOrBooking()
    {
        await using var db = CreateDbContext();
        var (_, booking) = await SeedBookingAsync(db, BookingStatus.Expired, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(-10));

        const long orderCode = 88881;
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, booking.TotalAmount, orderCode, Now.AddMinutes(-15));
        tx.MarkExpired("Booking payment hold expired.", Now.AddMinutes(-10));
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
        reloadedTx!.Status.Should().Be(PaymentTransactionStatus.Expired);
        reloadedTx.RawResponse.Should().NotBeNullOrWhiteSpace();

        // Decision 8: Booking remains Expired and is never reinstated
        var reloadedBooking = await db.Bookings.FindAsync(booking.Id);
        reloadedBooking!.Status.Should().Be(BookingStatus.Expired);
        reloadedBooking.PaymentStatus.Should().Be(PaymentStatus.Unpaid);
    }

    [Fact]
    public async Task PayOsWebhook_LateSuccessAfterCancellation_DoesNotReopenTransactionOrConfirmBooking()
    {
        await using var db = CreateDbContext();
        var (_, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));

        const long orderCode = 88883;
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, booking.TotalAmount, orderCode, Now);
        tx.MarkCancelled("Traveler cancelled.", Now.AddMinutes(1));
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        var processor = new PaymentCompletionProcessor(db, new FixedClock(Now.AddMinutes(2)));
        var completed = await processor.CompleteAsync(
            transactionId: tx.Id,
            orderCode: null,
            outcome: PaymentTransactionStatus.Success,
            gatewayRef: "PAYOS-LATE-REF",
            rawResponse: "Late success notification.",
            verifiedAmount: booking.TotalAmount,
            verifiedCurrency: "VND",
            cancellationToken: CancellationToken.None);

        completed.IsSuccess.Should().BeTrue();
        completed.Value.StatusChanged.Should().BeFalse();

        var reloadedTx = await db.PaymentTransactions.FindAsync(tx.Id);
        reloadedTx!.Status.Should().Be(PaymentTransactionStatus.Cancelled);
        reloadedTx.GatewayTransactionRef.Should().Be("PAYOS-LATE-REF");

        var reloadedBooking = await db.Bookings.FindAsync(booking.Id);
        reloadedBooking!.Status.Should().Be(BookingStatus.PendingPayment);
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

    [Fact]
    public async Task MockOutcome_Success_IssuesOneValidTicketWithoutAQrCode()
    {
        await using var db = CreateDbContext();
        var (travelerId, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.Mock, booking.TotalAmount, 9995, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();
        var processor = new PaymentCompletionProcessor(db, new FixedClock(Now));
        var handler = new CompleteMockPaymentCommandHandler(
            db, new TestCurrentUserService(travelerId), processor, new PaymentOptions { Provider = "Mock" });

        var result = await handler.Handle(new CompleteMockPaymentCommand(tx.Id, "Success"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.ClearTrackedEntities();
        var ticket = (await db.Tickets.Where(candidate => candidate.BookingId == booking.Id).ToListAsync())
            .Should().ContainSingle().Subject;
        ticket.Status.Should().Be(TicketStatus.Valid);
        ticket.TicketCode.Should().Be(Ticket.CodeFor(booking.Id));
        ticket.IssuedAtUtc.Should().Be(Now);
        (await db.TicketQrCodes.AnyAsync()).Should().BeFalse("the QR is issued when the traveler opens the ticket");
    }

    [Fact]
    public async Task PayOsWebhook_DuplicateSuccess_IssuesOnlyOneTicket()
    {
        await using var db = CreateDbContext();
        var (_, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, booking.TotalAmount, 88884, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();
        var processor = new PaymentCompletionProcessor(db, new FixedClock(Now));

        await CompleteAsync(processor, tx.Id, PaymentTransactionStatus.Success, booking.TotalAmount);
        await CompleteAsync(processor, tx.Id, PaymentTransactionStatus.Success, booking.TotalAmount);

        (await db.Tickets.CountAsync(ticket => ticket.BookingId == booking.Id)).Should().Be(1);
    }

    [Theory]
    [InlineData(PaymentTransactionStatus.Cancelled)]
    [InlineData(PaymentTransactionStatus.Expired)]
    [InlineData(PaymentTransactionStatus.Failed)]
    public async Task NonSuccessOutcome_IssuesNoTicket(PaymentTransactionStatus outcome)
    {
        await using var db = CreateDbContext();
        var (_, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, booking.TotalAmount, 88885, Now);
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        await CompleteAsync(new PaymentCompletionProcessor(db, new FixedClock(Now)), tx.Id, outcome,
            booking.TotalAmount);

        (await db.Tickets.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Success_AfterTheBookingHoldExpired_IssuesNoTicket()
    {
        await using var db = CreateDbContext();
        var (_, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(-1));
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, booking.TotalAmount, 88886,
            Now.AddMinutes(-10));
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        await CompleteAsync(new PaymentCompletionProcessor(db, new FixedClock(Now)), tx.Id,
            PaymentTransactionStatus.Success, booking.TotalAmount);

        (await db.Bookings.FindAsync(booking.Id))!.Status.Should().Be(BookingStatus.Expired);
        (await db.Tickets.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task LateSuccess_AfterATerminalTransaction_IssuesNoTicket()
    {
        await using var db = CreateDbContext();
        var (_, booking) = await SeedBookingAsync(db, BookingStatus.PendingPayment, PaymentStatus.Unpaid,
            paymentExpiresAtUtc: Now.AddMinutes(15));
        var tx = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, booking.TotalAmount, 88887, Now);
        tx.MarkCancelled("Traveler cancelled.", Now.AddMinutes(1));
        db.PaymentTransactions.Add(tx);
        await db.SaveChangesAsync();

        await CompleteAsync(new PaymentCompletionProcessor(db, new FixedClock(Now.AddMinutes(2))), tx.Id,
            PaymentTransactionStatus.Success, booking.TotalAmount);

        (await db.Tickets.AnyAsync()).Should().BeFalse();
    }

    private static Task<Result<PaymentCompletionResult>> CompleteAsync(PaymentCompletionProcessor processor,
        long transactionId, PaymentTransactionStatus outcome, decimal amount) =>
        processor.CompleteAsync(transactionId, orderCode: null, outcome, gatewayRef: "REF", rawResponse: "raw",
            verifiedAmount: amount, verifiedCurrency: "VND", CancellationToken.None);

    private static TestDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static void SeedBookableTourWithStops(TestDbContext db, long scheduleId, DateTimeOffset departureStartUtc)
    {
        var category = PoiCategory.Create("Beach", null);
        typeof(PoiCategory).GetProperty(nameof(PoiCategory.Id))!.SetValue(category, 31);
        var poiOne = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poiOne, 501L);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Name))!.SetValue(poiOne, "Golden Bridge");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Latitude))!.SetValue(poiOne, 16.0m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Longitude))!.SetValue(poiOne, 108.0m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.CategoryId))!.SetValue(poiOne, 31);
        var poiTwo = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poiTwo, 502L);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Name))!.SetValue(poiTwo, "Marble Mountains");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Latitude))!.SetValue(poiTwo, 16.0m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Longitude))!.SetValue(poiTwo, 108.2m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.CategoryId))!.SetValue(poiTwo, 31);

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 11L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 7L);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        typeof(Tour).GetProperty(nameof(Tour.TimeZoneId))!.SetValue(tour, "Asia/Ho_Chi_Minh");
        var stopOne = TourItineraryItem.Create(1, 1, 501, new TimeOnly(17, 30), 60, "Golden Bridge walk");
        var stopTwo = TourItineraryItem.Create(1, 2, 502, new TimeOnly(19, 0), 45, null);
        var itineraryItemsField = typeof(Tour).GetField("_itineraryItems",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((List<TourItineraryItem>)itineraryItemsField.GetValue(tour)!).AddRange([stopOne, stopTwo]);

        var schedule = TourSchedule.Create(departureStartUtc, departureStartUtc.AddHours(8),
            "Da Nang", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, scheduleId);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);

        db.PoiCategories.Add(category);
        db.PointsOfInterest.AddRange(poiOne, poiTwo);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
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

        public PaymentLinkRequest? LastRequest { get; private set; }

        public Action? OnCreate { get; init; }

        public Task<PaymentLinkResult> CreatePaymentLinkAsync(
            PaymentLinkRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            OnCreate?.Invoke();
            return Task.FromResult(new PaymentLinkResult(
                PaymentLinkId: $"MOCK-LINK-{request.OrderCode}",
                CheckoutUrl: $"http://localhost:3001/checkout/mock?paymentId={request.OrderCode}&bookingId={request.BookingId}",
                ExpiresAtUtc: request.ExpiresAtUtc));
        }
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
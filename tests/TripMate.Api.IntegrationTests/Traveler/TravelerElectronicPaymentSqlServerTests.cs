using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.Traveler.Payments.Create;
using TripMate.Application.Features.Traveler.Payments.Mock;
using TripMate.Application.Features.Traveler.Payments.Status;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Traveler;

public sealed class TravelerElectronicPaymentSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PaymentTransactions_IndexesAndColumns_ExistOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        var orderCodeIndexCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.indexes
            WHERE name = 'UX_PaymentTransactions_OrderCode'
              AND object_id = OBJECT_ID('payment.PaymentTransactions');
            """);
        orderCodeIndexCount.Should().Be(1);

        var pendingIndexCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.indexes
            WHERE name = 'UX_PaymentTransactions_Booking_Pending'
              AND object_id = OBJECT_ID('payment.PaymentTransactions');
            """);
        pendingIndexCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateOrReuse_OnSqlServer_ReusesActivePendingTransaction()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (travelerId, bookingId) = await SeedBookingAsync(database);

        var clock = new FixedClock(Now);
        var gateway = new FakePaymentGateway();
        var options = new PaymentOptions { Provider = "Mock" };

        await using (var context = database.CreateDbContext())
        {
            var handler = new CreateOrReusePaymentLinkCommandHandler(
                context, new TestCurrentUserService(travelerId), clock, gateway, options);

            var first = await handler.Handle(new CreateOrReusePaymentLinkCommand(bookingId), CancellationToken.None);
            var second = await handler.Handle(new CreateOrReusePaymentLinkCommand(bookingId), CancellationToken.None);

            first.IsSuccess.Should().BeTrue();
            second.IsSuccess.Should().BeTrue();
            second.Value.PaymentId.Should().Be(first.Value.PaymentId);
            second.Value.CheckoutUrl.Should().Be(first.Value.CheckoutUrl);
        }

        await using (var verifyContext = database.CreateDbContext())
        {
            var count = await verifyContext.PaymentTransactions
                .CountAsync(t => t.BookingId == bookingId);
            count.Should().Be(1);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CompleteMockPayment_OnSqlServer_ConfirmsBookingAndPersistsStatus()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (travelerId, bookingId) = await SeedBookingAsync(database);

        var clock = new FixedClock(Now);
        var gateway = new FakePaymentGateway();
        var options = new PaymentOptions { Provider = "Mock" };

        long paymentId;
        await using (var context = database.CreateDbContext())
        {
            var createHandler = new CreateOrReusePaymentLinkCommandHandler(
                context, new TestCurrentUserService(travelerId), clock, gateway, options);
            var createResult = await createHandler.Handle(new CreateOrReusePaymentLinkCommand(bookingId), CancellationToken.None);
            createResult.IsSuccess.Should().BeTrue();
            paymentId = createResult.Value.PaymentId;
        }

        await using (var context = database.CreateDbContext())
        {
            var processor = new PaymentCompletionProcessor(context, clock);
            var mockHandler = new CompleteMockPaymentCommandHandler(
                context, new TestCurrentUserService(travelerId), processor, options);

            var outcomeResult = await mockHandler.Handle(new CompleteMockPaymentCommand(paymentId, "Success"), CancellationToken.None);
            outcomeResult.IsSuccess.Should().BeTrue();
            outcomeResult.Value.Status.Should().Be("Success");
            outcomeResult.Value.BookingStatus.Should().Be("Confirmed");
            outcomeResult.Value.PaymentStatus.Should().Be("Paid");
        }

        await using (var verifyContext = database.CreateDbContext())
        {
            var booking = await verifyContext.Bookings.FindAsync(bookingId);
            booking!.Status.Should().Be(BookingStatus.Confirmed);
            booking.PaymentStatus.Should().Be(PaymentStatus.Paid);

            var tx = await verifyContext.PaymentTransactions.FindAsync(paymentId);
            tx!.Status.Should().Be(PaymentTransactionStatus.Success);

            var ticket = (await verifyContext.Tickets.Where(candidate => candidate.BookingId == bookingId)
                .ToListAsync()).Should().ContainSingle().Subject;
            ticket.TicketCode.Should().Be(Ticket.CodeFor(bookingId));
            ticket.Status.Should().Be(TicketStatus.Valid);
            ticket.IssuedAtUtc.Should().Be(Now);
        }
    }

    private static async Task<(long TravelerId, long BookingId)> SeedBookingAsync(
        SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();

        var traveler = new User
        {
            Email = $"traveler-pay-{Guid.NewGuid():N}@test.local",
            FullName = "SQL Payment Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        };
        var operatorUser = new User
        {
            Email = $"operator-pay-{Guid.NewGuid():N}@test.local",
            FullName = "SQL Payment Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        context.Users.AddRange(traveler, operatorUser);
        await context.SaveChangesAsync();

        context.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            CompanyName = "SQL Payment Operator Co",
            TaxCode = $"TAX-{operatorUser.Id}",
            BusinessLicenseNo = $"LIC-{operatorUser.Id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await context.SaveChangesAsync();

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUser.Id);
        Set(tour, nameof(Tour.Title), "SQL Payment Tour");
        Set(tour, nameof(Tour.BasePrice), 1_500_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        context.Tours.Add(tour);
        await context.SaveChangesAsync();

        var schedule = TourSchedule.Create(
            Now.AddDays(7), Now.AddDays(8), null, 10, Now, adultPrice: 1_500_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        context.TourSchedules.Add(schedule);
        await context.SaveChangesAsync();

        var participant = BookingParticipant.Create(
            "SQL Participant", ParticipantType.Adult, null, null, null, null, null);
        var booking = Booking.Create(
            bookingCode: $"BK-{Now:yyyyMMdd}-{Guid.NewGuid():N}"[..25],
            travelerUserId: traveler.Id,
            tourScheduleId: schedule.Id,
            voucherId: null,
            unitPrice: 1_500_000m,
            discountAmount: 0m,
            totalAmount: 1_500_000m,
            participants: [participant],
            bookedAtUtc: Now,
            paymentExpiresAtUtc: Now.AddMinutes(20));

        context.Bookings.Add(booking);
        await context.SaveChangesAsync();

        return (traveler.Id, booking.Id);
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(instance, value);

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
        public DateTimeOffset UtcNow => initial;
    }

    private sealed class TestCurrentUserService(long userId) : ICurrentUserService
    {
        public long? UserId => userId;
        public string? Email => $"user-{userId}@test.local";
        public string? Role => nameof(UserRole.Traveler);
    }
}
using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Payouts.GetDetails;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutDetailsSqlServerTests
{
    private const string SeedSql = """
        DECLARE @operator BIGINT;
        INSERT dbo.Users(role, email, full_name, status)
        VALUES ('TourOperator', N'pd@example.com', N'Payout Operator', 'Active');
        SET @operator = SCOPE_IDENTITY();
        INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
        VALUES (@operator, N'Danang Tourist Co., Ltd', N'TAX-UC64', N'LIC-UC64');

        DECLARE @traveler BIGINT;
        INSERT dbo.Users(role, email, full_name, status)
        VALUES ('Traveler', N'pd-traveler@example.com', N'Payout Traveler', 'Active');
        SET @traveler = SCOPE_IDENTITY();

        DECLARE @tour BIGINT;
        INSERT commerce.Tours(operator_user_id, title, base_price, status)
        VALUES (@operator, N'Hoi An Night Walk', 500000, 'Approved');
        SET @tour = SCOPE_IDENTITY();

        DECLARE @schedule BIGINT;
        INSERT commerce.TourSchedules(tour_id, start_datetime, end_datetime, total_capacity)
        VALUES (@tour, '2026-08-10T08:00:00', '2026-08-10T12:00:00', 20);
        SET @schedule = SCOPE_IDENTITY();

        DECLARE @bookingA BIGINT;
        INSERT commerce.Bookings(booking_code, traveler_user_id, tour_schedule_id,
            quantity, unit_price, discount_amount, total_amount, status, payment_status)
        VALUES ('BK-UC64-A', @traveler, @schedule, 2, 500000, 0, 1000000, 'Completed', 'Paid');
        SET @bookingA = SCOPE_IDENTITY();
        DECLARE @bookingB BIGINT;
        INSERT commerce.Bookings(booking_code, traveler_user_id, tour_schedule_id,
            quantity, unit_price, discount_amount, total_amount, status, payment_status)
        VALUES ('BK-UC64-B', @traveler, NULL, 1, 300000, 0, 300000, 'Completed', 'PartiallyRefunded');
        SET @bookingB = SCOPE_IDENTITY();

        DECLARE @payout BIGINT;
        INSERT payment.Payouts(operator_user_id, period_start, period_end,
            gross_revenue, commission_amount, net_amount, status, requested_at)
        VALUES (@operator, '2026-08-01', '2026-08-31', 10000000, 1000000, 9000000, 'Requested', '2026-09-01T02:30:00');
        SET @payout = SCOPE_IDENTITY();

        INSERT payment.PayoutItems(payout_id, booking_id, amount)
        VALUES (@payout, @bookingA, 8000000), (@payout, @bookingB, 2000000);

        INSERT payment.PaymentTransactions(booking_id, gateway, amount, transaction_type, status)
        VALUES (@bookingA, 'VNPAY', 4000000, 'Payment', 'Success'),
               (@bookingA, 'VNPAY', 6000000, 'Payment', 'Success'),
               (@bookingA, 'VNPAY', 999999, 'Payment', 'Failed'),
               (@bookingB, 'MoMo', 300000, 'Payment', 'Success');

        INSERT payment.Refunds(booking_id, amount, initiated_by, status)
        VALUES (@bookingB, 100000, 'TourOperator', 'Processed'),
               (@bookingB, 50000, 'Administrator', 'Pending');
        """;

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_AssemblesBreakdownWithSumsAndWritesAccessAudit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(SeedSql);
        await using var db = database.CreateDbContext();
        var handler = new GetPayoutDetailsQueryHandler(db, new Administrator(), new Clock());
        var payoutId = await db.Payouts.AsNoTracking().Select(payout => (long)payout.Id).SingleAsync();

        var result = await handler.Handle(new GetPayoutDetailsQuery(payoutId), default);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.PayoutId.Should().Be(payoutId.ToString());
        detail.PayoutCode.Should().Be($"PO-{payoutId}");
        detail.Operator.CompanyName.Should().Be("Danang Tourist Co., Ltd");
        detail.CommissionRate.Should().Be(10.00m);
        detail.GrossRevenue.Should().Be(10000000m);
        detail.NetAmount.Should().Be(9000000m);
        detail.Status.Should().Be("Requested");
        detail.RequestedAtUtc.Should().NotBeNull();

        detail.Bookings.Should().HaveCount(2);
        detail.Bookings[0].BookingCode.Should().Be("BK-UC64-A", "ordered by booking code");
        detail.Bookings[0].TourName.Should().Be("Hoi An Night Walk");
        detail.Bookings[0].PaidAmount.Should().Be(10000000m, "two successful payments summed; the Failed one excluded");
        detail.Bookings[0].RefundedAmount.Should().Be(0m);
        detail.Bookings[0].NetAmount.Should().Be(8000000m, "the engine's per-booking payout amount");
        detail.Bookings[1].BookingCode.Should().Be("BK-UC64-B");
        detail.Bookings[1].TourName.Should().BeNull("the booking has no schedule; the Web renders Not available");
        detail.Bookings[1].PaidAmount.Should().Be(300000m);
        detail.Bookings[1].RefundedAmount.Should().Be(100000m, "only the Processed refund counts");
        detail.Bookings[1].NetAmount.Should().Be(2000000m);

        var audits = await db.AuditLogs.AsNoTracking()
            .Where(log => log.ActionType == AuditActionTypes.PayoutDetailsViewed
                && log.AffectedEntity == AuditEntityTypes.Payout
                && log.AffectedEntityId == payoutId)
            .ToListAsync();
        audits.Should().ContainSingle("BR-130 audits every successful details access");
        audits[0].Result.Should().Be(AuditOutcome.Success);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_AuditPersistenceFailure_FailsClosedWithoutBreakdown()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(SeedSql);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(database.ConnectionString)
            .Options;
        await using var failing = new FailingSaveDbContext(options);
        var handler = new GetPayoutDetailsQueryHandler(failing, new Administrator(), new Clock());
        long payoutId;
        await using (var probe = database.CreateDbContext())
        {
            payoutId = await probe.Payouts.AsNoTracking().Select(payout => (long)payout.Id).SingleAsync();
        }

        var act = async () => await handler.Handle(new GetPayoutDetailsQuery(payoutId), default);

        await act.Should().ThrowAsync<DbUpdateException>("BR-130 is fail-closed: no audit row, no breakdown");
    }

    private sealed class FailingSaveDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("audit write failed");
    }

    private sealed class Administrator : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => "Administrator";
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    }
}
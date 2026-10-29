using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Bookings.Get;
using TripMate.Application.Features.Operator.Bookings.List;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Bookings;

/// <summary>
/// UC-40 translation tests against the real schema. The InMemory unit tests cannot prove LIKE
/// escaping of "[", UTC datetime2 boundaries, the aggregate queries, or ordering tie-breaks.
/// Skipped automatically when no SQL Server connection string is configured.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class OperatorBookingsSqlServerTests
{
    private const string SeedScript = """
        DECLARE @alice BIGINT, @fun BIGINT, @bob BIGINT, @opA BIGINT, @opB BIGINT;
        INSERT dbo.Users(role, email, full_name, status, phone_number)
        VALUES ('Traveler', N'alice@uc40.example', N'Alice Booker', 'Active', N'+84901234567');
        SET @alice = SCOPE_IDENTITY();
        INSERT dbo.Users(role, email, full_name, status)
        VALUES ('Traveler', N'fun@uc40.example', N'100% Fun_Seeker', 'Active');
        SET @fun = SCOPE_IDENTITY();
        INSERT dbo.Users(role, email, full_name, status)
        VALUES ('Traveler', N'bob@uc40.example', N'Bob Booker', 'Active');
        SET @bob = SCOPE_IDENTITY();
        INSERT dbo.Users(role, email, full_name, status)
        VALUES ('TourOperator', N'opa@uc40.example', N'Operator A', 'Active');
        SET @opA = SCOPE_IDENTITY();
        INSERT dbo.Users(role, email, full_name, status)
        VALUES ('TourOperator', N'opb@uc40.example', N'Operator B', 'Active');
        SET @opB = SCOPE_IDENTITY();
        INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no, approval_status)
        VALUES (@opA, N'Company A', N'TAX-UC40-A', N'LIC-UC40-A', 'Approved'),
               (@opB, N'Company B', N'TAX-UC40-B', N'LIC-UC40-B', 'Approved');

        DECLARE @tourA BIGINT, @tourB BIGINT;
        INSERT commerce.Tours(operator_user_id, title, base_price, duration_days, status)
        VALUES (@opA, N'Tour A', 500000, 1, 'Approved');
        SET @tourA = SCOPE_IDENTITY();
        INSERT commerce.Tours(operator_user_id, title, base_price, duration_days, status)
        VALUES (@opB, N'Tour B', 500000, 1, 'Approved');
        SET @tourB = SCOPE_IDENTITY();

        -- Vietnam is UTC+7: local 2 Nov spans 2026-11-01T17:00 (inclusive) to 2026-11-02T17:00 (exclusive).
        DECLARE @sBefore BIGINT, @sIn1 BIGINT, @sIn2 BIGINT, @sAfter BIGINT, @sB BIGINT;
        INSERT commerce.TourSchedules(tour_id, start_datetime, end_datetime, total_capacity, status)
        VALUES (@tourA, '2026-11-01T16:59:59', '2026-11-02T08:00:00', 50, 'Scheduled');
        SET @sBefore = SCOPE_IDENTITY();
        INSERT commerce.TourSchedules(tour_id, start_datetime, end_datetime, total_capacity, status)
        VALUES (@tourA, '2026-11-01T17:30:00', '2026-11-02T08:00:00', 50, 'Scheduled');
        SET @sIn1 = SCOPE_IDENTITY();
        INSERT commerce.TourSchedules(tour_id, start_datetime, end_datetime, total_capacity, status)
        VALUES (@tourA, '2026-11-02T16:59:59', '2026-11-03T08:00:00', 50, 'Scheduled');
        SET @sIn2 = SCOPE_IDENTITY();
        INSERT commerce.TourSchedules(tour_id, start_datetime, end_datetime, total_capacity, status)
        VALUES (@tourA, '2026-11-02T17:00:00', '2026-11-03T08:00:00', 50, 'Scheduled');
        SET @sAfter = SCOPE_IDENTITY();
        INSERT commerce.TourSchedules(tour_id, start_datetime, end_datetime, total_capacity, status)
        VALUES (@tourB, '2026-11-02T01:00:00', '2026-11-02T08:00:00', 50, 'Scheduled');
        SET @sB = SCOPE_IDENTITY();

        INSERT commerce.Bookings(booking_code, traveler_user_id, tour_schedule_id, quantity,
            unit_price, discount_amount, total_amount, status, payment_status, booked_at)
        VALUES ('BK-UC40-BEFORE', @alice, @sBefore, 1, 500000, 0, 500000, 'Confirmed', 'Paid', '2026-10-20T08:00:00'),
               ('BK-UC40-IN1',    @alice, @sIn1,    2, 500000, 0, 1000000, 'Confirmed', 'Paid', '2026-10-21T08:00:00'),
               ('BK-UC40-IN2',    @fun,   @sIn2,    3, 500000, 0, 1500000, 'Completed', 'Paid', '2026-10-21T08:00:00'),
               ('BK-UC40-AFTER',  @alice, @sAfter,  1, 500000, 0, 500000, 'Cancelled', 'Refunded', '2026-10-22T08:00:00'),
               ('BK-UC40-OTHER',  @bob,   @sB,      9, 500000, 0, 4500000, 'Confirmed', 'Paid', '2026-10-22T08:00:00');

        DECLARE @in1 BIGINT = (SELECT booking_id FROM commerce.Bookings WHERE booking_code = 'BK-UC40-IN1');
        DECLARE @in2 BIGINT = (SELECT booking_id FROM commerce.Bookings WHERE booking_code = 'BK-UC40-IN2');

        INSERT commerce.BookingParticipants(booking_id, full_name, phone_number, email, participant_type,
            id_document_type, id_document_number, nationality)
        VALUES (@in1, N'Nguyen Van A', N'+84900000001', N'a@secret.example', 'Adult', 'Passport',
                N'ciphertext-P1234567', 'VNM'),
               (@in1, N'Nguyen Thi B', NULL, NULL, 'Child', NULL, NULL, NULL);

        -- UX_Tickets_OneTicketPerBooking allows one ticket per booking: IN1 is checked in, IN2 has an unused ticket.
        INSERT commerce.Tickets(booking_id, ticket_code, status, issued_at, checked_in_at)
        VALUES (@in1, 'TK-UC40-IN1-A', 'Used', '2026-10-21T08:01:00', '2026-11-02T01:00:00'),
               (@in2, 'TK-UC40-IN2-A', 'Valid', '2026-10-21T08:01:00', NULL);

        INSERT payment.PaymentTransactions(booking_id, gateway, gateway_transaction_ref, amount, transaction_type,
            status, order_code, raw_response, checkout_url, created_at, succeeded_at)
        VALUES (@in1, 'VNPAY', N'OLD-REF', 1000000, 'Payment', 'Success', 9001, N'gateway-secret',
                N'https://pay.example/secret', '2026-10-21T08:00:00', '2026-10-21T08:00:30'),
               (@in1, 'PayOS', N'NEW-REF', 1000000, 'Payment', 'Success', 9002, N'gateway-secret',
                N'https://pay.example/secret', '2026-10-21T08:05:00', '2026-10-21T08:05:30');

        -- Same booked_at on two more bookings of operator A: booking id is the stable tie-breaker.
        INSERT commerce.Bookings(booking_code, traveler_user_id, tour_schedule_id, quantity,
            unit_price, discount_amount, total_amount, status, payment_status, booked_at)
        VALUES ('BK-UC40-TIE1', @alice, @sIn1, 1, 500000, 0, 500000, 'PendingPayment', 'Unpaid', '2026-10-25T08:00:00'),
               ('BK-UC40-TIE2', @alice, @sIn1, 1, 500000, 0, 500000, 'PendingPayment', 'Unpaid', '2026-10-25T08:00:00');
        """;

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task List_IsolatesOperatorAndSummarisesOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(SeedScript);
        await using var db = database.CreateDbContext();
        var handler = new GetOperatorBookingsQueryHandler(db, new OperatorA(db));

        var result = await handler.Handle(new GetOperatorBookingsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(item => item.BookingCode).Should().NotContain("BK-UC40-OTHER");
        result.Value.TotalCount.Should().Be(6);
        result.Value.Summary.TotalBookings.Should().Be(6);
        // Confirmed + Completed only: BEFORE(1) + IN1(2) + IN2(3) participants and 500k + 1m + 1.5m.
        result.Value.Summary.TotalParticipants.Should().Be(6);
        result.Value.Summary.TotalConfirmedAmount.Should().Be(3_000_000m);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task List_OrdersByBookedAtThenBookingIdDescendingOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(SeedScript);
        await using var db = database.CreateDbContext();
        var handler = new GetOperatorBookingsQueryHandler(db, new OperatorA(db));

        var result = await handler.Handle(new GetOperatorBookingsQuery(PageSize: 2), CancellationToken.None);

        result.Value.Items.Select(item => item.BookingCode).Should().Equal("BK-UC40-TIE2", "BK-UC40-TIE1");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task List_DepartureRangeUsesVietnamDayBoundariesOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(SeedScript);
        await using var db = database.CreateDbContext();
        var handler = new GetOperatorBookingsQueryHandler(db, new OperatorA(db));
        var day = new DateOnly(2026, 11, 2);

        var result = await handler.Handle(
            new GetOperatorBookingsQuery(DepartureFrom: day, DepartureTo: day, PageSize: 100), CancellationToken.None);

        result.Value.Items.Select(item => item.BookingCode).Should().BeEquivalentTo(
            "BK-UC40-IN1", "BK-UC40-IN2", "BK-UC40-TIE1", "BK-UC40-TIE2");
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData("%", "BK-UC40-IN2")]
    [InlineData("_", "BK-UC40-IN2")]
    [InlineData("[a]", "")]
    [InlineData("100% fun", "BK-UC40-IN2")]
    [InlineData("ALICE@UC40", "BK-UC40-BEFORE,BK-UC40-IN1,BK-UC40-AFTER,BK-UC40-TIE1,BK-UC40-TIE2")]
    [InlineData("bk-uc40-in1", "BK-UC40-IN1")]
    public async Task List_KeywordEscapesLikeWildcardsOnSqlServer(string keyword, string expectedCodes)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(SeedScript);
        await using var db = database.CreateDbContext();
        var handler = new GetOperatorBookingsQueryHandler(db, new OperatorA(db));

        var result = await handler.Handle(
            new GetOperatorBookingsQuery(Keyword: keyword, PageSize: 100), CancellationToken.None);

        string[] expected = expectedCodes.Length == 0 ? [] : expectedCodes.Split(',');
        result.Value.Items.Select(item => item.BookingCode).Should().BeEquivalentTo(expected);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task List_DerivesCheckInFromTicketOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(SeedScript);
        await using var db = database.CreateDbContext();
        var handler = new GetOperatorBookingsQueryHandler(db, new OperatorA(db));

        var result = await handler.Handle(new GetOperatorBookingsQuery(PageSize: 100), CancellationToken.None);

        var byCode = result.Value.Items.ToDictionary(item => item.BookingCode);
        byCode["BK-UC40-IN1"].CheckInStatus.Should().Be("CheckedIn");
        byCode["BK-UC40-IN1"].CheckedInAtUtc.Should().Be(new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero));
        byCode["BK-UC40-IN2"].CheckInStatus.Should().Be("NotCheckedIn");
        byCode["BK-UC40-BEFORE"].CheckInStatus.Should().Be("NotCheckedIn");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_MapsPaymentAndParticipantsWithoutSensitiveDataOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(SeedScript);
        await using var db = database.CreateDbContext();
        var listHandler = new GetOperatorBookingsQueryHandler(db, new OperatorA(db));
        var detailHandler = new GetOperatorBookingQueryHandler(db, new OperatorA(db));
        var list = await listHandler.Handle(new GetOperatorBookingsQuery(Keyword: "bk-uc40-in1"), CancellationToken.None);
        long bookingId = long.Parse(list.Value.Items.Single().BookingId, System.Globalization.CultureInfo.InvariantCulture);

        var result = await detailHandler.Handle(new GetOperatorBookingQuery(bookingId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Booker.PhoneNumber.Should().Be("+84901234567");
        result.Value.Participants.Select(participant => participant.FullName)
            .Should().Equal("Nguyen Van A", "Nguyen Thi B");
        result.Value.Payment!.GatewayTransactionRef.Should().Be("NEW-REF");
        result.Value.Payment.Gateway.Should().Be(PaymentGateway.PayOS);
        result.Value.PaidAmount.Should().Be(1_000_000m);
        result.Value.CheckInStatus.Should().Be("CheckedIn");
        result.Value.CanCancel.Should().BeFalse();
        result.Value.CancelBlockedReason.Should().Be("CheckedIn");

        string json = JsonSerializer.Serialize(result.Value);
        foreach (string forbidden in new[]
                 {
                     "P1234567", "ciphertext", "VNM", "+84900000001", "a@secret.example", "gateway-secret",
                     "pay.example", "9001", "9002",
                 })
        {
            json.Should().NotContain(forbidden);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Detail_BookingOfAnotherOperatorIsForbiddenOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(SeedScript);
        await using var db = database.CreateDbContext();
        var otherId = db.Bookings.Where(booking => booking.BookingCode == "BK-UC40-OTHER").Select(booking => booking.Id).Single();
        var handler = new GetOperatorBookingQueryHandler(db, new OperatorA(db));

        var result = await handler.Handle(new GetOperatorBookingQuery(otherId), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("operator_booking.forbidden");
    }

    /// <summary>Signs in as the seeded "Operator A" (resolved by e-mail so the test never hard-codes an identity value).</summary>
    private sealed class OperatorA : ICurrentUserService
    {
        public OperatorA(IApplicationDbContext db)
        {
            UserId = db.Users.Where(user => user.Email == "opa@uc40.example").Select(user => (long?)user.Id).Single();
        }

        public long? UserId { get; }

        public string? Role => nameof(UserRole.TourOperator);
    }
}
using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.Bookings.GetList;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Bookings;

[Collection(nameof(TripMateApiFactory))]
public sealed class PlatformBookingsSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_FiltersAndPagesPlatformBookingsWithStableOrderingInSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'uc62-traveler@example.com', N'Lantern Traveler', 'Active');
            SET @traveler = SCOPE_IDENTITY();

            DECLARE @operator BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'uc62-operator@example.com', N'Lantern Operator', 'Active');
            SET @operator = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no)
            VALUES (@operator, N'Lantern Co.', N'TAX-UC62-SQL', N'LIC-UC62-SQL');

            DECLARE @tour BIGINT;
            INSERT commerce.Tours(operator_user_id, title, base_price, duration_days, status)
            VALUES (@operator, N'Lantern Festival Tour', 500000, 1, 'Approved');
            SET @tour = SCOPE_IDENTITY();

            DECLARE @schedule BIGINT;
            INSERT commerce.TourSchedules(tour_id, start_datetime, end_datetime, total_capacity, status)
            VALUES (@tour, '2026-11-10T01:00:00', '2026-11-10T09:00:00', 10, 'Scheduled');
            SET @schedule = SCOPE_IDENTITY();

            INSERT commerce.Bookings(booking_code, traveler_user_id, tour_schedule_id, quantity,
                unit_price, discount_amount, total_amount, status, payment_status, booked_at)
            VALUES ('BK-UC62-1', @traveler, @schedule, 1, 500000, 50000, 450000,
                    'Confirmed', 'Paid', '2026-10-23T08:00:00'),
                   ('BK-UC62-2', @traveler, @schedule, 1, 500000, 0, 500000,
                    'Confirmed', 'Paid', '2026-10-23T08:00:00'),
                   ('BK-UC62-3', @traveler, @schedule, 1, 500000, 0, 500000,
                    'Cancelled', 'Refunded', '2026-10-22T08:00:00');
            """);
        await using var db = database.CreateDbContext();
        var handler = new GetPlatformBookingsQueryHandler(db, new Staff());

        var filtered = await handler.Handle(new GetPlatformBookingsQuery(
            Keyword: "lantern",
            Status: BookingStatus.Confirmed,
            PaymentStatus: PaymentStatus.Paid,
            PageSize: 1),
            CancellationToken.None);

        filtered.IsSuccess.Should().BeTrue();
        filtered.Value.TotalCount.Should().Be(2);
        filtered.Value.TotalPages.Should().Be(2);
        var first = filtered.Value.Items.Should().ContainSingle().Subject;
        first.BookingCode.Should().Be("BK-UC62-2", "same-time bookings use booking ID as the stable final tie-breaker");
        first.SubtotalAmount.Should().Be(500000m);
        first.DiscountAmount.Should().Be(0m);
        first.TotalAmount.Should().Be(500000m);
        first.Traveler.Email.Should().Be("uc62-traveler@example.com");
        first.Tour.Title.Should().Be("Lantern Festival Tour");
        first.Tour.OperatorName.Should().Be("Lantern Co.");

        var secondPage = await handler.Handle(new GetPlatformBookingsQuery(
            Status: BookingStatus.Confirmed,
            PaymentStatus: PaymentStatus.Paid,
            PageNumber: 2,
            PageSize: 1),
            CancellationToken.None);

        secondPage.Value.Items.Should().ContainSingle().Which.BookingCode.Should().Be("BK-UC62-1");
    }

    private sealed class Staff : ICurrentUserService
    {
        public long? UserId => 1;
        public string? Role => nameof(UserRole.Staff);
    }
}
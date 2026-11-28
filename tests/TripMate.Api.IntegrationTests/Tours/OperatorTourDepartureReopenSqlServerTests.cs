using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tours;

[Collection(nameof(TripMateApiFactory))]
public sealed class OperatorTourDepartureReopenSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AddDeparture_OnCompletedPublishedTour_PreservesHistoryAndEnablesNewBookings()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedCompletedTourAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var operatorClient = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var foreignOperatorClient = factory.CreateAuthenticatedClient(seed.ForeignOperatorId, UserRole.TourOperator);
        using var travelerClient = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);

        var startLocalDate = DateTime.SpecifyKind(DateTimeOffset.UtcNow.AddDays(14).UtcDateTime.Date, DateTimeKind.Unspecified);
        var endLocalDate = DateTime.SpecifyKind(DateTimeOffset.UtcNow.AddDays(16).UtcDateTime.Date, DateTimeKind.Unspecified);
        var startUtc = new DateTimeOffset(startLocalDate, TimeSpan.FromHours(7)).ToUniversalTime();
        var endUtc = new DateTimeOffset(endLocalDate.AddHours(23).AddMinutes(59).AddSeconds(59), TimeSpan.FromHours(7)).ToUniversalTime();
        var deadlineUtc = startUtc.AddDays(-2);
        var idempotencyKey = Guid.NewGuid();

        var payload = new
        {
            startAtUtc = startUtc,
            endAtUtc = endUtc,
            meetingPoint = "Cổng Công viên Biển Đông, Đà Nẵng",
            totalCapacity = 16,
            minimumCapacity = 4,
            registrationDeadlineAtUtc = deadlineUtc,
            adultPrice = 3_900_000m,
            childPrice = 2_400_000m,
            infantPrice = 0m,
            meetingLatitude = 16.0678m,
            meetingLongitude = 108.2462m,
        };

        // Foreign operator gets 404 without disclosure
        using var foreignReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operator/tours/{seed.TourId}/departures")
        {
            Content = JsonContent.Create(payload),
        };
        foreignReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var foreignRes = await foreignOperatorClient.SendAsync(foreignReq);
        foreignRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Traveler gets 403
        using var travelerForbiddenReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operator/tours/{seed.TourId}/departures")
        {
            Content = JsonContent.Create(payload),
        };
        travelerForbiddenReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var travelerForbiddenRes = await travelerClient.SendAsync(travelerForbiddenReq);
        travelerForbiddenRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Owner adds new departure (plus idempotency replay)
        using var req1 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operator/tours/{seed.TourId}/departures")
        {
            Content = JsonContent.Create(payload),
        };
        req1.Headers.Add("Idempotency-Key", idempotencyKey.ToString());
        using var res1 = await operatorClient.SendAsync(req1);
        res1.StatusCode.Should().Be(HttpStatusCode.Created, await res1.Content.ReadAsStringAsync());

        using var resJson1 = JsonDocument.Parse(await res1.Content.ReadAsStringAsync());
        long newScheduleId = resJson1.RootElement.GetProperty("scheduleId").GetInt64();
        resJson1.RootElement.GetProperty("isReplay").GetBoolean().Should().BeFalse();

        using var reqReplay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operator/tours/{seed.TourId}/departures")
        {
            Content = JsonContent.Create(payload),
        };
        reqReplay.Headers.Add("Idempotency-Key", idempotencyKey.ToString());
        using var resReplay = await operatorClient.SendAsync(reqReplay);
        resReplay.StatusCode.Should().Be(HttpStatusCode.Created);
        using var replayJson = JsonDocument.Parse(await resReplay.Content.ReadAsStringAsync());
        replayJson.RootElement.GetProperty("scheduleId").GetInt64().Should().Be(newScheduleId);
        replayJson.RootElement.GetProperty("isReplay").GetBoolean().Should().BeTrue();

        // Duplicate start time with a new key -> 409
        using var reqDup = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operator/tours/{seed.TourId}/departures")
        {
            Content = JsonContent.Create(payload),
        };
        reqDup.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var resDup = await operatorClient.SendAsync(reqDup);
        resDup.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Public detail returns both new bookable schedule and preserved review/highlights/requirements
        using var publicDetailRes = await travelerClient.GetAsync($"/api/v1/tours/{seed.TourId}");
        publicDetailRes.StatusCode.Should().Be(HttpStatusCode.OK);
        using var publicJson = JsonDocument.Parse(await publicDetailRes.Content.ReadAsStringAsync());
        publicJson.RootElement.GetProperty("hasBookableSchedule").GetBoolean().Should().BeTrue();
        publicJson.RootElement.GetProperty("highlights").GetString().Should().Be("Bà Nà & Phố cổ");
        publicJson.RootElement.GetProperty("travellerRequirements").GetString().Should().Be("Mang giày đi bộ");
        publicJson.RootElement.GetProperty("reviews").GetArrayLength().Should().Be(1);
        var schedules = publicJson.RootElement.GetProperty("schedules");
        schedules.GetArrayLength().Should().Be(1);
        schedules[0].GetProperty("scheduleId").GetString().Should().Be(newScheduleId.ToString());
        schedules[0].GetProperty("bookable").GetBoolean().Should().BeTrue();
        schedules[0].GetProperty("minimumCapacity").GetInt32().Should().Be(4);

        // Traveler books the new departure
        using var bookReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/traveler/tour-bookings")
        {
            Content = JsonContent.Create(new
            {
                tourScheduleId = newScheduleId.ToString(),
                participants = new object[]
                {
                    new { fullName = "Nguyễn Văn A", participantType = "Adult", phoneNumber = "0905111222", email = "a@test.local" },
                    new { fullName = "Bé B", participantType = "Child" },
                },
            }),
        };
        bookReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var bookRes = await travelerClient.SendAsync(bookReq);
        bookRes.StatusCode.Should().Be(HttpStatusCode.Created, await bookRes.Content.ReadAsStringAsync());
        using var bookJson = JsonDocument.Parse(await bookRes.Content.ReadAsStringAsync());
        long newBookingId = long.Parse(bookJson.RootElement.GetProperty("bookingId").GetString()!);
        bookJson.RootElement.GetProperty("totalAmount").GetDecimal().Should().Be(6_300_000m);

        // Verify SQL Server historical integrity
        await using var verification = database.CreateDbContext();
        var oldSchedule = await verification.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == seed.OldScheduleId);
        oldSchedule.Status.Should().Be(TourScheduleStatus.Completed);
        oldSchedule.ReservedCapacity.Should().Be(2);
        oldSchedule.AdultPrice.Should().Be(3_200_000m);

        var newSchedule = await verification.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == newScheduleId);
        newSchedule.Status.Should().Be(TourScheduleStatus.Scheduled);
        newSchedule.ReservedCapacity.Should().Be(2);
        newSchedule.AdultPrice.Should().Be(3_900_000m);

        var oldBooking = await verification.Bookings.AsNoTracking().SingleAsync(b => b.Id == seed.OldBookingId);
        oldBooking.TourScheduleId.Should().Be(seed.OldScheduleId);
        oldBooking.Status.Should().Be(BookingStatus.Completed);
        oldBooking.PaymentStatus.Should().Be(PaymentStatus.Paid);
        oldBooking.TotalAmount.Should().Be(6_400_000m);

        var newBooking = await verification.Bookings.AsNoTracking().SingleAsync(b => b.Id == newBookingId);
        newBooking.TourScheduleId.Should().Be(newScheduleId);
        newBooking.Status.Should().Be(BookingStatus.PendingPayment);
        newBooking.TotalAmount.Should().Be(6_300_000m);

        (await verification.PaymentTransactions.CountAsync(p => p.BookingId == seed.OldBookingId && p.Status == PaymentTransactionStatus.Success)).Should().Be(1);
        (await verification.TripReviews.CountAsync(r => r.BookingId == seed.OldBookingId && r.TourId == seed.TourId)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateThenDeleteDraft_SucceedsWithoutForeignKeyErrors()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedCompletedTourAsync(database);
        await using var factory = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var operatorClient = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        var startUtc = DateTimeOffset.UtcNow.AddDays(20);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/operator/tours")
        {
            Content = JsonContent.Create(new
            {
                title = "Draft deletion test",
                description = "To be deleted cleanly",
                basePrice = 1_000_000m,
                durationDays = 1,
                destinationIds = new[] { seed.DestinationId },
                cancellationPolicyTemplateId = seed.PolicyTemplateId,
                itineraryItems = new[] { new { dayNo = 1, sequenceNo = 1, poiId = seed.PoiId, plannedTime = "08:00:00", stayDurationMinutes = 60 } },
                schedules = new[] { new { startAtUtc = startUtc, endAtUtc = startUtc.AddHours(8), totalCapacity = 10, minimumCapacity = 1 } },
            }),
        };
        createReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var created = await operatorClient.SendAsync(createReq);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        long draftTourId = createdJson.RootElement.GetProperty("tourId").GetInt64();

        using var deleted = await operatorClient.DeleteAsync($"/api/v1/operator/tours/{draftTourId}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());

        await using var verification = database.CreateDbContext();
        (await verification.Tours.AnyAsync(t => t.Id == draftTourId)).Should().BeFalse();
    }

    private static async Task<Seed> SeedCompletedTourAsync(SqlServerTestDatabase database)
    {
        var operatorId = await database.ExecuteScalarAsync<long>("""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'reopen-op@test.local', N'Reopen Operator', 'Active');
            DECLARE @id BIGINT = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no, approval_status)
            VALUES (@id, N'Da Nang Heritage Tours', N'REOPEN01', N'LIC-REOPEN-01', 'Approved');
            SELECT CONVERT(BIGINT, @id);
            """);

        var foreignOperatorId = await database.ExecuteScalarAsync<long>("""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('TourOperator', N'reopen-foreign@test.local', N'Foreign Operator', 'Active');
            DECLARE @id BIGINT = SCOPE_IDENTITY();
            INSERT dbo.OperatorProfiles(user_id, company_name, tax_code, business_license_no, approval_status)
            VALUES (@id, N'Other Tours', N'REOPEN02', N'LIC-REOPEN-02', 'Approved');
            SELECT CONVERT(BIGINT, @id);
            """);

        var travelerId = await database.ExecuteScalarAsync<long>("""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'reopen-traveler@test.local', N'Lê Minh Hoàng', 'Active');
            DECLARE @id BIGINT = SCOPE_IDENTITY();
            INSERT dbo.TravelerProfiles(user_id) VALUES (@id);
            SELECT CONVERT(BIGINT, @id);
            """);

        var staffId = await database.ExecuteScalarAsync<long>("""
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Staff', N'reopen-staff@test.local', N'Mod Staff', 'Active');
            SELECT CONVERT(BIGINT, SCOPE_IDENTITY());
            """);

        var destinationId = await database.ExecuteScalarAsync<long>("""
            INSERT catalog.Destinations(name) VALUES (N'Đà Nẵng');
            SELECT CONVERT(BIGINT, SCOPE_IDENTITY());
            """);

        var poiId = await database.ExecuteScalarAsync<long>($"""
            INSERT catalog.POICategories(name) VALUES (N'Di sản');
            DECLARE @catId BIGINT = SCOPE_IDENTITY();
            INSERT catalog.POIs(name, category_id, latitude, longitude, status, created_by)
            VALUES (N'Phố cổ Hội An', @catId, 15.8801, 108.3380, 'Active', {operatorId});
            SELECT CONVERT(BIGINT, SCOPE_IDENTITY());
            """);

        var policyId = await database.ExecuteScalarAsync<long>("""
            SELECT TOP (1) cancellation_policy_template_id FROM commerce.CancellationPolicyTemplates ORDER BY cancellation_policy_template_id;
            """);

        var pastStart = DateTimeOffset.UtcNow.AddDays(-20);
        var pastEnd = DateTimeOffset.UtcNow.AddDays(-17);
        var publishedAt = DateTimeOffset.UtcNow.AddDays(-40);
        static string SqlUtc(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", global::System.Globalization.CultureInfo.InvariantCulture);

        var publishedSql = SqlUtc(publishedAt);
        var pastStartSql = SqlUtc(pastStart);
        var pastEndSql = SqlUtc(pastEnd);
        var bookedAtSql = SqlUtc(pastStart.AddDays(-5));
        var reviewedAtSql = SqlUtc(pastEnd.AddDays(1));

        var tourId = await database.ExecuteScalarAsync<long>($"""
            INSERT commerce.Tours(
                operator_user_id, title, destination_summary, description, base_price, duration_days,
                time_zone_id, highlights, traveller_requirements, inclusions, exclusions,
                cancellation_policy_template_id, status, reviewed_by, reviewed_at, published_at)
            VALUES (
                {operatorId}, N'Đà Nẵng - Hội An 3 Ngày 2 Đêm', N'Đà Nẵng',
                N'Hành trình khám phá miền Trung.', 3500000, 3,
                N'Asia/Ho_Chi_Minh', N'Bà Nà & Phố cổ', N'Mang giày đi bộ',
                N'Khách sạn 4 sao', N'Chi phí cá nhân',
                {policyId}, 'Published', {staffId}, '{publishedSql}', '{publishedSql}');
            DECLARE @tid BIGINT = SCOPE_IDENTITY();
            UPDATE commerce.Tours SET version_root_tour_id = @tid, version_no = 1 WHERE tour_id = @tid;
            INSERT commerce.TourDestinations(tour_id, destination_id, sequence_no) VALUES (@tid, {destinationId}, 1);
            INSERT commerce.TourCancellationPolicyRules(tour_id, days_before_departure, cancellation_fee_percent)
            SELECT @tid, days_before_departure, cancellation_fee_percent
            FROM commerce.CancellationPolicyTemplateRules WHERE cancellation_policy_template_id = {policyId};
            INSERT commerce.TourItineraryItems(tour_id, day_no, sequence_no, poi_id, planned_time, stay_duration_minutes, notes)
            VALUES (@tid, 1, 1, {poiId}, '09:00:00', 120, N'Xe du lịch | Tham quan buổi sáng');
            INSERT commerce.TourMedia(tour_id, cloudinary_public_id, delivery_url, alt_text, sort_order, is_primary, lifecycle_status)
            VALUES (@tid, N'reopen-test/cover', N'https://cdn.example.test/cover.jpg', N'Cover', 1, 1, 'Active');
            SELECT CONVERT(BIGINT, @tid);
            """);

        var oldScheduleId = await database.ExecuteScalarAsync<long>($"""
            INSERT commerce.TourSchedules(
                tour_id, start_datetime, end_datetime, meeting_point, total_capacity, minimum_capacity,
                adult_price, reserved_capacity, status)
            VALUES (
                {tourId}, '{pastStartSql}', '{pastEndSql}', N'Sân bay Đà Nẵng', 10, 2, 3200000, 2, 'Completed');
            SELECT CONVERT(BIGINT, SCOPE_IDENTITY());
            """);

        var oldBookingId = await database.ExecuteScalarAsync<long>($"""
            INSERT commerce.Bookings(
                booking_code, traveler_user_id, tour_schedule_id, quantity, unit_price, discount_amount,
                total_amount, status, payment_status, booked_at)
            VALUES (
                'BK-HIST-001', {travelerId}, {oldScheduleId}, 2, 3200000, 0,
                6400000, 'Completed', 'Paid', '{bookedAtSql}');
            DECLARE @bid BIGINT = SCOPE_IDENTITY();
            INSERT commerce.BookingParticipants(booking_id, full_name, participant_type)
            VALUES (@bid, N'Lê Minh Hoàng', 'Adult'), (@bid, N'Trần Thị Hoa', 'Adult');
            INSERT payment.PaymentTransactions(
                booking_id, gateway, gateway_transaction_ref, amount, currency, transaction_type, status, order_code, created_at, succeeded_at)
            VALUES (
                @bid, 'Mock', N'MOCK-HIST-001', 6400000, 'VND', 'Payment', 'Success', 900001, '{bookedAtSql}', '{bookedAtSql}');
            INSERT social.TripReviews(
                booking_id, traveler_user_id, tour_id, overall_rating, title, content,
                publish_display_name, public_display_name, publication_status, created_at, edit_deadline, updated_at)
            VALUES (
                @bid, {travelerId}, {tourId}, 5, N'Tuyệt vời', N'Trải nghiệm rất tốt',
                1, N'Lê Minh Hoàng', 'Published', '{reviewedAtSql}', DATEADD(day, 7, '{reviewedAtSql}'), '{reviewedAtSql}');
            SELECT CONVERT(BIGINT, @bid);
            """);

        return new Seed(operatorId, foreignOperatorId, travelerId, destinationId, poiId, policyId, tourId, oldScheduleId, oldBookingId);
    }

    private sealed record Seed(
        long OperatorId,
        long ForeignOperatorId,
        long TravelerId,
        long DestinationId,
        long PoiId,
        long PolicyTemplateId,
        long TourId,
        long OldScheduleId,
        long OldBookingId);
}
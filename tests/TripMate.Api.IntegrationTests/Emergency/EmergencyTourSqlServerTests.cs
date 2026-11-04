using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Emergency;

public sealed class EmergencyTourSqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private const string OperatorPassword = "OperatorPass123!";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SameIdempotencyKey_SamePayload_CommitsOnlyOneOperationAndReplaysSafely()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        using var factory = CreateFactory(database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var preview = await client.GetAsync($"{operatorBase}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(preview, "impactVersion");

        string key = Guid.NewGuid().ToString();
        var payload = new
        {
            impactVersion,
            reason = "WeatherEmergency",
            reasonDetails = "Storm making departure impossible.",
            emergencyContact = "0900000000",
            evidenceReference = "storm-ref-1",
        };

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var firstResponse = await client.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", payload);
        using var secondResponse = await client.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", payload);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        string firstId = await ReadPropertyAsync(firstResponse, "operationId");
        string secondId = await ReadPropertyAsync(secondResponse, "operationId");
        firstId.Should().Be(secondId);

        // Verify SQL Server state: exactly one operation, one audit log, and no refund
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations
            WHERE tour_schedule_id = {seed.ScheduleId};
            """)).Should().Be(1);

        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM payment.Refunds
            WHERE booking_id = {seed.BookingId};
            """)).Should().Be(0);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.AuditLogs
            WHERE action_type = 'TOUR_EMERGENCY_CANCELLED';
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SameIdempotencyKey_DifferentPayload_ReturnsIdempotencyPayloadMismatch()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        using var factory = CreateFactory(database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var preview = await client.GetAsync($"{operatorBase}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(preview, "impactVersion");

        string key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var firstResponse = await client.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", new
        {
            impactVersion,
            reason = "WeatherEmergency",
            reasonDetails = "Initial storm warning details.",
            emergencyContact = "0900000000",
        });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var secondResponse = await client.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", new
        {
            impactVersion,
            reason = "WeatherEmergency",
            reasonDetails = "Different payload text with same idempotency key.",
            emergencyContact = "0900000000",
        });
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        string body = await secondResponse.Content.ReadAsStringAsync();
        body.Should().Contain("tour_schedule.idempotency_payload_mismatch");

        // Exactly one operation remains in database
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations
            WHERE tour_schedule_id = {seed.ScheduleId};
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentConfirmations_OnlyOneRequestWins()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var factory = CreateFactory(database.ConnectionString);
        using var previewClient = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var preview = await previewClient.GetAsync($"{operatorBase}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(preview, "impactVersion");

        using var client1 = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var client2 = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        client1.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        client2.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var task1 = client1.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", new
        {
            impactVersion,
            reason = "WeatherEmergency",
            reasonDetails = "Concurrent attempt 1.",
            emergencyContact = "0900000000",
        });
        var task2 = client2.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", new
        {
            impactVersion,
            reason = "WeatherEmergency",
            reasonDetails = "Concurrent attempt 2.",
            emergencyContact = "0900000000",
        });

        var responses = await Task.WhenAll(task1, task2);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, "exactly one confirmation must succeed");
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1, "the concurrent conflict must be rejected");

        // Verify exactly one operation committed in database
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations
            WHERE tour_schedule_id = {seed.ScheduleId};
            """)).Should().Be(1);

        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM payment.Refunds
            WHERE booking_id = {seed.BookingId};
            """)).Should().Be(0, "a decision creates no refund");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.Notifications;
            """)).Should().Be(1, "exactly one notification must be committed for the booking owner, not for operator");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task StaleImpactVersion_ReturnsConcurrentStateChanged()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        using var factory = CreateFactory(database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var preview = await client.GetAsync($"{operatorBase}/emergency-impact");
        string staleImpactVersion = await ReadPropertyAsync(preview, "impactVersion");

        // Mutate departure state in SQL Server directly to simulate concurrent booking reservation
        await database.ExecuteNonQueryAsync($"""
            UPDATE commerce.TourSchedules
            SET reserved_capacity = reserved_capacity + 1
            WHERE schedule_id = {seed.ScheduleId};
            """);

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", new
        {
            impactVersion = staleImpactVersion,
            reason = "WeatherEmergency",
            reasonDetails = "Attempt with stale impact version.",
            emergencyContact = "0900000000",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("tour_schedule.concurrent_state_changed");

        // Verify departure was NOT cancelled
        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.TourSchedules WHERE schedule_id = {seed.ScheduleId};
            """)).Should().Be("Scheduled");

        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations
            WHERE tour_schedule_id = {seed.ScheduleId};
            """)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task TransactionFailure_RollbacksAllState()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        var faultInterceptor = new AuditLogFailureInterceptor();
        var seed = await SeedAsync(database, Now.AddDays(5));
        using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            dateTimeProviderFactory: _ => new FixedDateTimeProvider(Now),
            saveChangesInterceptor: faultInterceptor);

        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var preview = await client.GetAsync($"{operatorBase}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(preview, "impactVersion");

        faultInterceptor.Arm(); // Arm fault to throw inside SaveChangesAsync
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", new
        {
            impactVersion,
            reason = "WeatherEmergency",
            reasonDetails = "Fault injection scenario.",
            emergencyContact = "0900000000",
        });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // Verify atomic rollback in SQL Server: schedule, booking, notifications, audit, operations
        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.TourSchedules WHERE schedule_id = {seed.ScheduleId};
            """)).Should().Be("Scheduled", "schedule must roll back to Scheduled");

        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.Bookings WHERE booking_id = {seed.BookingId};
            """)).Should().Be("Confirmed", "booking must roll back to Confirmed");

        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations
            WHERE tour_schedule_id = {seed.ScheduleId};
            """)).Should().Be(0, "operation must not be committed");

        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM payment.Refunds
            WHERE booking_id = {seed.BookingId};
            """)).Should().Be(0, "a decision creates no refund");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.Notifications;
            """)).Should().Be(0, "notifications must roll back");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.AuditLogs
            WHERE action_type = 'TOUR_EMERGENCY_CANCELLED';
            """)).Should().Be(0, "audit must roll back");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UnverifiedTripSessionLink_ReturnsLinkUnverified_WithoutMutation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddHours(-2), seedUnverifiedSession: true);

        using var factory = CreateFactory(database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var preview = await client.GetAsync($"{operatorBase}/emergency-impact");
        string impactVersion = await ReadPropertyAsync(preview, "impactVersion");

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var stopResponse = await client.PostAsJsonAsync($"{operatorBase}/emergency-stop", new
        {
            impactVersion,
            currentPassword = OperatorPassword,
            reason = "SafetyIncident",
            reasonDetails = "Incident during active trip with unverified session link.",
            immediateInstructions = "Stay with guide.",
            emergencyContact = "0900000000",
        });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        string body = await stopResponse.Content.ReadAsStringAsync();
        body.Should().Contain("trip_session.link_unverified");

        // Verify zero mutations in SQL Server
        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.TourSchedules WHERE schedule_id = {seed.ScheduleId};
            """)).Should().Be("Scheduled");

        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.Bookings WHERE booking_id = {seed.BookingId};
            """)).Should().Be("Confirmed");

        (await database.ExecuteScalarAsync<string>($"""
            SELECT fsm_state FROM trip.TripSessions WHERE session_id = {seed.UnverifiedSessionId};
            """)).Should().Be(TripSession.NavigatingState, "unverified session must remain in Navigating state");

        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations
            WHERE tour_schedule_id = {seed.ScheduleId};
            """)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CancelBeforeDeparture_WithMultipleCaptures_LeavesPaymentsUntouchedOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5), paymentAmounts: [60_000m, 40_000m]);
        using var factory = CreateFactory(database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var preview = await client.GetAsync($"{operatorBase}/emergency-impact");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        string impactVersion = await ReadPropertyAsync(preview, "impactVersion");

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", new
        {
            impactVersion,
            reason = "TransportFailure",
            reasonDetails = "Vehicle broke down before departure.",
            emergencyContact = "0900000000",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.Bookings WHERE booking_id = {seed.BookingId};
            """)).Should().Be("Cancelled");
        (await database.ExecuteScalarAsync<string>($"""
            SELECT payment_status FROM commerce.Bookings WHERE booking_id = {seed.BookingId};
            """)).Should().Be("Paid", "payment state belongs to the refund workflow");
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM payment.Refunds WHERE booking_id = {seed.BookingId};
            """)).Should().Be(0);
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM payment.PaymentTransactions
            WHERE booking_id = {seed.BookingId} AND transaction_type <> 'Payment';
            """)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CancelBeforeDeparture_WithZeroCaptures_CreatesNoRefundOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5), isBookingPaid: false);
        using var factory = CreateFactory(database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var preview = await client.GetAsync($"{operatorBase}/emergency-impact");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        string impactVersion = await ReadPropertyAsync(preview, "impactVersion");

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.PostAsJsonAsync($"{operatorBase}/cancel-before-departure", new
        {
            impactVersion,
            reason = "WeatherEmergency",
            reasonDetails = "Typhoon prevents departure.",
            emergencyContact = "0900000000",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM payment.Refunds WHERE booking_id = {seed.BookingId};
            """)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task EmergencyStopActive_SiblingDepartureSessionIsNotInterrupted()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddHours(-2));
        await using var db = database.CreateDbContext();

        // Create verified itinerary and session for target booking
        var targetItinerary = Itinerary.CreateManual(seed.TravelerId, "Target Itinerary", Itinerary.ActiveStatus, Now.AddDays(-1));
        Set(targetItinerary, nameof(Itinerary.SourceTourId), seed.TourId);
        Set(targetItinerary, nameof(Itinerary.SourceBookingId), seed.BookingId);
        var targetItem = ItineraryItem.CreateRest(1, Now.AddHours(-2), Now.AddHours(2), "Rest stop target");
        targetItinerary.AddItem(targetItem);
        db.Itineraries.Add(targetItinerary);
        await db.SaveChangesAsync();

        var targetSessionItem = TripSessionItem.Snapshot(targetItem.Id, 1, 1, "Stop", 16m, 108m, Now.AddHours(-2), Now.AddHours(2), true);
        var targetSession = TripSession.Start(targetItinerary.Id, targetItinerary.Id, seed.TravelerId, Guid.NewGuid(),
            Now.AddHours(-2), Now.AddHours(4), [targetSessionItem]);
        db.TripSessions.Add(targetSession);

        // Create sibling traveler, schedule, booking, itinerary and active session
        var siblingTraveler = new User
        {
            Email = $"uc70-sibling-traveler-{Guid.NewGuid():N}@test.local",
            FullName = "UC70 Sibling Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.Add(siblingTraveler);
        await db.SaveChangesAsync();

        var siblingSchedule = TourSchedule.Create(
            Now.AddHours(-1), Now.AddHours(7), "Da Nang", 20, Now, adultPrice: 100_000m);
        Set(siblingSchedule, nameof(TourSchedule.TourId), seed.TourId);
        siblingSchedule.ReserveCapacity(1);
        db.TourSchedules.Add(siblingSchedule);
        await db.SaveChangesAsync();

        var siblingBooking = Booking.Create(
            siblingTraveler.Id, siblingSchedule.Id, 1, 100_000m,
            BookingStatus.Confirmed, PaymentStatus.Paid);
        Set(siblingBooking, nameof(Booking.BookingCode), $"BK-{Guid.NewGuid():N}"[..20]);
        Set(siblingBooking, nameof(Booking.TourSchedule), siblingSchedule);
        db.Bookings.Add(siblingBooking);
        await db.SaveChangesAsync();

        var siblingItinerary = Itinerary.CreateManual(siblingTraveler.Id, "Sibling Itinerary", Itinerary.ActiveStatus, Now.AddDays(-1));
        Set(siblingItinerary, nameof(Itinerary.SourceTourId), seed.TourId);
        Set(siblingItinerary, nameof(Itinerary.SourceBookingId), siblingBooking.Id);
        var siblingItem = ItineraryItem.CreateRest(1, Now.AddHours(-1), Now.AddHours(3), "Rest stop sibling");
        siblingItinerary.AddItem(siblingItem);
        db.Itineraries.Add(siblingItinerary);
        await db.SaveChangesAsync();

        var siblingSessionItem = TripSessionItem.Snapshot(siblingItem.Id, 1, 1, "Stop", 16m, 108m, Now.AddHours(-1), Now.AddHours(3), true);
        var siblingSession = TripSession.Start(siblingItinerary.Id, siblingItinerary.Id, siblingTraveler.Id, Guid.NewGuid(),
            Now.AddHours(-1), Now.AddHours(5), [siblingSessionItem]);
        db.TripSessions.Add(siblingSession);
        await db.SaveChangesAsync();

        using var factory = CreateFactory(database.ConnectionString);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        using var preview = await client.GetAsync($"{operatorBase}/emergency-impact");
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        string impactVersion = await ReadPropertyAsync(preview, "impactVersion");

        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var stopResponse = await client.PostAsJsonAsync($"{operatorBase}/emergency-stop", new
        {
            impactVersion,
            currentPassword = OperatorPassword,
            reason = "SafetyIncident",
            reasonDetails = "Landslide blocked target route.",
            immediateInstructions = "Return to bus.",
            emergencyContact = "0900000000",
        });

        stopResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify target departure is stopped and target session interrupted
        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.TourSchedules WHERE schedule_id = {seed.ScheduleId};
            """)).Should().Be("EmergencyStopped");

        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.Bookings WHERE booking_id = {seed.BookingId};
            """)).Should().Be("Interrupted");

        (await database.ExecuteScalarAsync<string>($"""
            SELECT fsm_state FROM trip.TripSessions WHERE session_id = {targetSession.Id};
            """)).Should().Be(TripSession.InterruptedState);

        // Verify sibling departure and session are UNTOUCHED
        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.TourSchedules WHERE schedule_id = {siblingSchedule.Id};
            """)).Should().Be("Scheduled");

        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.Bookings WHERE booking_id = {siblingBooking.Id};
            """)).Should().Be("Confirmed");

        (await database.ExecuteScalarAsync<string>($"""
            SELECT fsm_state FROM trip.TripSessions WHERE session_id = {siblingSession.Id};
            """)).Should().Be(TripSession.NavigatingState);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Uc14_Or_AnonymousUser_CannotTrigger_EmergencyTourCancellation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        using var factory = CreateFactory(database.ConnectionString);
        string operatorBase = $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}";

        // 1. Anonymous request without authentication
        using var anonClient = factory.CreateClient();
        using var anonResponse = await anonClient.GetAsync($"{operatorBase}/emergency-impact");
        anonResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 2. Traveler user trying to call operator emergency endpoint
        using var travelerClient = factory.CreateAuthenticatedClient(seed.TravelerId, UserRole.Traveler);
        using var travelerResponse = await travelerClient.GetAsync($"{operatorBase}/emergency-impact");
        travelerResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static TripMateApiFactory CreateFactory(string sqlServerConnectionString) => new(
        sqlServerConnectionString: sqlServerConnectionString,
        dateTimeProviderFactory: _ => new FixedDateTimeProvider(Now));

    private static async Task<string> ReadPropertyAsync(
        HttpResponseMessage response,
        string propertyName)
    {
        string payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty(propertyName).ToString();
    }

    private static async Task<Seed> SeedAsync(
        SqlServerTestDatabase database,
        DateTimeOffset start,
        bool seedUnverifiedSession = false,
        decimal[]? paymentAmounts = null,
        bool isBookingPaid = true)
    {
        await using var db = database.CreateDbContext();
        var hasher = new PasswordHasherService();
        var operatorUser = new User
        {
            Email = $"uc70-operator-{Guid.NewGuid():N}@test.local",
            FullName = "UC70 Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            PasswordHash = hasher.Hash(OperatorPassword),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var travelerUser = new User
        {
            Email = $"uc70-traveler-{Guid.NewGuid():N}@test.local",
            FullName = "UC70 Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.AddRange(operatorUser, travelerUser);
        await db.SaveChangesAsync();

        var operatorProfile = new OperatorProfile
        {
            UserId = operatorUser.Id,
            CompanyName = "UC70 Tours",
            TaxCode = "UC70-TAX",
            BusinessLicenseNo = "UC70-LICENSE",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.OperatorProfiles.Add(operatorProfile);

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUser.Id);
        Set(tour, nameof(Tour.Title), "UC70 SQL tour");
        Set(tour, nameof(Tour.BasePrice), 100_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        Set(tour, nameof(Tour.CreatedAtUtc), Now);
        Set(tour, nameof(Tour.UpdatedAtUtc), Now);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        var schedule = TourSchedule.Create(
            start, start.AddHours(8), "Da Nang", 20, Now,
            adultPrice: 100_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        schedule.ReserveCapacity(1);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var booking = Booking.Create(
            travelerUser.Id, schedule.Id, 1, 100_000m,
            BookingStatus.Confirmed, isBookingPaid ? PaymentStatus.Paid : PaymentStatus.Unpaid);
        Set(booking, nameof(Booking.BookingCode), $"BK-{Guid.NewGuid():N}"[..20]);
        Set(booking, nameof(Booking.TourSchedule), schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        if (isBookingPaid)
        {
            var amounts = paymentAmounts ?? [100_000m];
            int orderCode = 1000;
            foreach (decimal amount in amounts)
            {
                var payment = PaymentTransaction.Create(
                    booking.Id, PaymentGateway.PayOS, amount, orderCode++, Now);
                payment.MarkSuccess($"paid-{booking.Id}-{orderCode}", null, Now, 0m);
                db.PaymentTransactions.Add(payment);
            }
            await db.SaveChangesAsync();
        }

        long? unverifiedSessionId = null;
        if (seedUnverifiedSession)
        {
            var unverifiedItinerary = Itinerary.CreateManual(travelerUser.Id, "Legacy unverified", Itinerary.ActiveStatus, Now.AddDays(-1));
            Set(unverifiedItinerary, nameof(Itinerary.SourceTourId), tour.Id);
            var itineraryItem = ItineraryItem.CreateRest(1, Now.AddHours(-2), Now.AddHours(2), "Rest stop");
            unverifiedItinerary.AddItem(itineraryItem);
            db.Itineraries.Add(unverifiedItinerary);
            await db.SaveChangesAsync();

            var sessionItem = TripSessionItem.Snapshot(itineraryItem.Id, 1, 1, "Stop", 16m, 108m, Now.AddHours(-2), Now.AddHours(2), true);
            var session = TripSession.Start(1, unverifiedItinerary.Id, travelerUser.Id, Guid.NewGuid(),
                Now.AddHours(-2), Now.AddHours(4), [sessionItem]);
            db.TripSessions.Add(session);
            await db.SaveChangesAsync();
            unverifiedSessionId = session.Id;
        }

        return new Seed(operatorUser.Id, travelerUser.Id, tour.Id, schedule.Id, booking.Id, unverifiedSessionId);
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(
            property,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);

    private sealed record Seed(
        long OperatorId,
        long TravelerId,
        long TourId,
        long ScheduleId,
        long BookingId,
        long? UnverifiedSessionId = null);

    private sealed class FixedDateTimeProvider(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class AuditLogFailureInterceptor : SaveChangesInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed && eventData.Context?.ChangeTracker.Entries<AuditLog>().Any() == true)
            {
                throw new InvalidOperationException("Simulated transaction fault on saving AuditLog.");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CancelBeforeDeparture_ClosesEveryOpenReviewOfThatDepartureOnly_AfterCommit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        var otherOperator = await SeedAsync(database, Now.AddDays(5));
        var reviews = await AddReviewsInEveryStateAsync(database, seed);
        long sibling = await AddSiblingScheduleReviewAsync(database, seed);
        long otherReview = (await AddReviewsInEveryStateAsync(database, otherOperator)).Pending;
        var notifier = new CommitCheckingNotifier(database);
        using var factory = CreateFactory(database.ConnectionString, notifier);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var response = await PostCancelAsync(client, seed, await ReadImpactVersionAsync(client, seed));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (long id in new[] { reviews.Pending, reviews.Acknowledged, reviews.Escalated, reviews.AlreadyClosed })
            (await ReviewStatusAsync(database, id)).Should().Be(WeatherSafetyReview.StatusClosed);
        (await ReviewStatusAsync(database, sibling)).Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        (await ReviewStatusAsync(database, otherReview)).Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM payment.Refunds WHERE booking_id = {seed.BookingId};
            """)).Should().Be(0);

        notifier.Closed.Select(call => call.ReviewId).Should().BeEquivalentTo(
            [reviews.Pending, reviews.Acknowledged, reviews.Escalated],
            "a review that was already closed has nothing new to announce");
        notifier.Closed.Should().OnlyContain(call => call.OperatorId == seed.OperatorId
            && call.ScheduleId == seed.ScheduleId
            && call.StatusAtNotify == WeatherSafetyReview.StatusClosed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task EmergencyStop_ClosesEveryOpenReviewOfThatDeparture_AfterCommit()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddHours(-2));
        var reviews = await AddReviewsInEveryStateAsync(database, seed);
        var notifier = new CommitCheckingNotifier(database);
        using var factory = CreateFactory(database.ConnectionString, notifier);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var response = await PostStopAsync(
            client, seed, await ReadImpactVersionAsync(client, seed), OperatorPassword);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.TourSchedules WHERE schedule_id = {seed.ScheduleId};
            """)).Should().Be("EmergencyStopped");
        foreach (long id in new[] { reviews.Pending, reviews.Acknowledged, reviews.Escalated })
            (await ReviewStatusAsync(database, id)).Should().Be(WeatherSafetyReview.StatusClosed);
        notifier.Closed.Select(call => call.ReviewId).Should().BeEquivalentTo(
            [reviews.Pending, reviews.Acknowledged, reviews.Escalated]);
        notifier.Closed.Should().OnlyContain(call => call.StatusAtNotify == WeatherSafetyReview.StatusClosed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ReplayedDecision_DoesNotRepeatTheReviewClosure()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        var reviews = await AddReviewsInEveryStateAsync(database, seed);
        var notifier = new CommitCheckingNotifier(database);
        using var factory = CreateFactory(database.ConnectionString, notifier);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string impactVersion = await ReadImpactVersionAsync(client, seed);
        string key = Guid.NewGuid().ToString();

        using var first = await PostCancelAsync(client, seed, impactVersion, key);
        using var replay = await PostCancelAsync(client, seed, impactVersion, key);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReviewStatusAsync(database, reviews.Pending)).Should().Be(WeatherSafetyReview.StatusClosed);
        notifier.Closed.Should().HaveCount(3, "only the committed decision announces the closure");
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations
            WHERE tour_schedule_id = {seed.ScheduleId};
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.AuditLogs WHERE action_type = 'TOUR_EMERGENCY_CANCELLED';
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Notifications;")).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentDecisions_CloseTheReviewOnce()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        var reviews = await AddReviewsInEveryStateAsync(database, seed);
        var notifier = new CommitCheckingNotifier(database);
        using var factory = CreateFactory(database.ConnectionString, notifier);
        using var client1 = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var client2 = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string impactVersion = await ReadImpactVersionAsync(client1, seed);

        var responses = await Task.WhenAll(
            PostCancelAsync(client1, seed, impactVersion),
            PostCancelAsync(client2, seed, impactVersion));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        (await ReviewStatusAsync(database, reviews.Pending)).Should().Be(WeatherSafetyReview.StatusClosed);
        notifier.Closed.Should().HaveCount(3);
        foreach (var response in responses) response.Dispose();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task WrongPassword_LeavesReviewsOpen()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddHours(-2));
        var reviews = await AddReviewsInEveryStateAsync(database, seed);
        var notifier = new CommitCheckingNotifier(database);
        using var factory = CreateFactory(database.ConnectionString, notifier);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var response = await PostStopAsync(
            client, seed, await ReadImpactVersionAsync(client, seed), "Not-The-Password-1!");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertReviewsStayOpenAsync(database, reviews, notifier);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task StaleImpactVersion_LeavesReviewsOpen()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        var reviews = await AddReviewsInEveryStateAsync(database, seed);
        var notifier = new CommitCheckingNotifier(database);
        using var factory = CreateFactory(database.ConnectionString, notifier);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var response = await PostCancelAsync(client, seed, new string('0', 64));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertReviewsStayOpenAsync(database, reviews, notifier);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task OtherOperator_LeavesReviewsOpen()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        var other = await SeedAsync(database, Now.AddDays(5));
        var reviews = await AddReviewsInEveryStateAsync(database, seed);
        var notifier = new CommitCheckingNotifier(database);
        using var factory = CreateFactory(database.ConnectionString, notifier);
        using var owner = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var client = factory.CreateAuthenticatedClient(other.OperatorId, UserRole.TourOperator);

        using var response = await PostCancelAsync(client, seed, await ReadImpactVersionAsync(owner, seed));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AssertReviewsStayOpenAsync(database, reviews, notifier);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task UnverifiedSession_LeavesReviewsOpen()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddHours(-2), seedUnverifiedSession: true);
        var reviews = await AddReviewsInEveryStateAsync(database, seed);
        var notifier = new CommitCheckingNotifier(database);
        using var factory = CreateFactory(database.ConnectionString, notifier);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);

        using var response = await PostStopAsync(
            client, seed, await ReadImpactVersionAsync(client, seed), OperatorPassword);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertReviewsStayOpenAsync(database, reviews, notifier);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RolledBackDecision_LeavesReviewsOpen()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database, Now.AddDays(5));
        var reviews = await AddReviewsInEveryStateAsync(database, seed);
        var notifier = new CommitCheckingNotifier(database);
        var faultInterceptor = new AuditLogFailureInterceptor();
        using var factory = CreateFactory(database.ConnectionString, notifier, faultInterceptor);
        using var client = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        string impactVersion = await ReadImpactVersionAsync(client, seed);

        faultInterceptor.Arm();
        using var response = await PostCancelAsync(client, seed, impactVersion);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.TourSchedules WHERE schedule_id = {seed.ScheduleId};
            """)).Should().Be("Scheduled");
        await AssertReviewsStayOpenAsync(database, reviews, notifier);
    }

    private static async Task AssertReviewsStayOpenAsync(
        SqlServerTestDatabase database,
        ReviewIds reviews,
        CommitCheckingNotifier notifier)
    {
        (await ReviewStatusAsync(database, reviews.Pending)).Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        (await ReviewStatusAsync(database, reviews.Acknowledged)).Should().Be(WeatherSafetyReview.StatusAcknowledged);
        (await ReviewStatusAsync(database, reviews.Escalated)).Should().Be(WeatherSafetyReview.StatusEscalated);
        notifier.Closed.Should().BeEmpty();
    }

    private static Task<string> ReviewStatusAsync(SqlServerTestDatabase database, long reviewId) =>
        database.ExecuteScalarAsync<string>($"""
            SELECT status FROM trip.WeatherSafetyReviews WHERE review_id = {reviewId};
            """);

    private static async Task<string> ReadImpactVersionAsync(HttpClient client, Seed seed)
    {
        using var preview = await client.GetAsync(
            $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}/emergency-impact");
        return await ReadPropertyAsync(preview, "impactVersion");
    }

    private static Task<HttpResponseMessage> PostCancelAsync(
        HttpClient client, Seed seed, string impactVersion, string? key = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}/cancel-before-departure")
        {
            Content = JsonContent.Create(new
            {
                impactVersion,
                reason = "WeatherEmergency",
                reasonDetails = "Storm making departure impossible.",
                emergencyContact = "0900000000",
            }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostStopAsync(
        HttpClient client, Seed seed, string impactVersion, string password)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/operator/tours/{seed.TourId}/schedules/{seed.ScheduleId}/emergency-stop")
        {
            Content = JsonContent.Create(new
            {
                impactVersion,
                currentPassword = password,
                reason = "SafetyIncident",
                reasonDetails = "Incident during the active trip.",
                immediateInstructions = "Stay with the guide.",
                emergencyContact = "0900000000",
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static async Task<ReviewIds> AddReviewsInEveryStateAsync(SqlServerTestDatabase database, Seed seed)
    {
        await using var db = database.CreateDbContext();
        var pending = NewReview(seed.ScheduleId, "pending");
        var acknowledged = NewReview(seed.ScheduleId, "acknowledged");
        acknowledged.Acknowledge(seed.OperatorId, Now);
        var escalated = NewReview(seed.ScheduleId, "escalated");
        escalated.Escalate(Now, "Desk");
        var closed = NewReview(seed.ScheduleId, "closed");
        closed.Close();
        db.WeatherSafetyReviews.AddRange(pending, acknowledged, escalated, closed);
        await db.SaveChangesAsync();
        return new ReviewIds(pending.Id, acknowledged.Id, escalated.Id, closed.Id);
    }

    private static async Task<long> AddSiblingScheduleReviewAsync(SqlServerTestDatabase database, Seed seed)
    {
        await using var db = database.CreateDbContext();
        var sibling = TourSchedule.Create(
            Now.AddDays(9), Now.AddDays(9).AddHours(8), "Da Nang", 20, Now, adultPrice: 100_000m);
        Set(sibling, nameof(TourSchedule.TourId), seed.TourId);
        db.TourSchedules.Add(sibling);
        await db.SaveChangesAsync();
        var review = NewReview(sibling.Id, "sibling");
        db.WeatherSafetyReviews.Add(review);
        await db.SaveChangesAsync();
        return review.Id;
    }

    private static WeatherSafetyReview NewReview(long scheduleId, string eventId) =>
        WeatherSafetyReview.CreateHold(scheduleId, "Tomorrow", eventId, Now.AddMinutes(-5), Now.AddMinutes(25));

    private static TripMateApiFactory CreateFactory(
        string sqlServerConnectionString,
        CommitCheckingNotifier notifier,
        SaveChangesInterceptor? saveChangesInterceptor = null) => new(
        sqlServerConnectionString: sqlServerConnectionString,
        dateTimeProviderFactory: _ => new FixedDateTimeProvider(Now),
        saveChangesInterceptor: saveChangesInterceptor,
        configureTestServices: services =>
        {
            services.RemoveAll<IWeatherSafetyReviewNotifier>();
            services.AddSingleton<IWeatherSafetyReviewNotifier>(notifier);
        });

    private sealed record ReviewIds(long Pending, long Acknowledged, long Escalated, long AlreadyClosed);

    private sealed record ClosedCall(long OperatorId, long ReviewId, long ScheduleId, string StatusAtNotify);

    /// <summary>
    /// Reads the review from another connection when notified. A notification sent inside the
    /// decision transaction would time out on the row lock, so this proves it was sent after commit.
    /// </summary>
    private sealed class CommitCheckingNotifier(SqlServerTestDatabase database) : IWeatherSafetyReviewNotifier
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<ClosedCall> _closed = new();

        public IReadOnlyCollection<ClosedCall> Closed => _closed.ToArray();

        public async Task NotifyChangedAsync(
            long operatorUserId,
            WeatherSafetyReviewChanged notification,
            CancellationToken cancellationToken)
        {
            if (notification.ChangeType != WeatherSafetyReviewChangeTypes.Closed) return;
            string status = await database.ExecuteScalarAsync<string>($"""
                SET LOCK_TIMEOUT 3000;
                SELECT status FROM trip.WeatherSafetyReviews WHERE review_id = {notification.ReviewId};
                """);
            _closed.Enqueue(new ClosedCall(
                operatorUserId, notification.ReviewId, notification.TourScheduleId, status));
        }
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
            TaxCode = $"UC70-{Guid.NewGuid():N}"[..20],
            BusinessLicenseNo = $"UC70-{Guid.NewGuid():N}"[..20],
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
            int orderCode = 1000 + ((int)booking.Id * 10);
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

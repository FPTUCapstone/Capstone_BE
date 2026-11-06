using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.WeatherSafetyReviews;
using TripMate.Application.Features.Weather.Processing;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;
using TripMate.Infrastructure.Weather;
using TripMate.Tests.Shared;

namespace TripMate.Api.IntegrationTests.Emergency;

public sealed class WeatherSafetyReviewSqlServerTests
{
    private const string AlertId = "alert-uc70-e2e";
    private const string StopLocationId = "loc-son-tra";

    // Production order: the background worker leases an item before processing it.
    private static async Task LeaseAndProcessAsync(TripMateApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWeatherAlertInboxStore>();
        var processor = scope.ServiceProvider.GetRequiredService<IWeatherAlertInboxProcessor>();
        foreach (var item in await store.LeasePendingItemsAsync(10, TimeSpan.FromMinutes(1), "e2e-worker", CancellationToken.None))
        {
            await processor.ProcessInboxItemAsync(item, CancellationToken.None);
        }
    }

    private static readonly DateTimeOffset Now = new(
        DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private sealed class FixedDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class RecordingWeatherSafetyReviewNotifier : IWeatherSafetyReviewNotifier
    {
        public List<(long OperatorUserId, WeatherSafetyReviewChanged Notification)> Calls { get; } = [];

        public Task NotifyChangedAsync(
            long operatorUserId,
            WeatherSafetyReviewChanged notification,
            CancellationToken cancellationToken)
        {
            Calls.Add((operatorUserId, notification));
            return Task.CompletedTask;
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ActiveTourSafetyHold_CanBeReadAndAcknowledgedByOwningOperator_AndEscalatesOnMissedSla()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);

        var notifier = new RecordingWeatherSafetyReviewNotifier();
        using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            dateTimeProviderFactory: _ => new FixedDateTimeProvider(Now),
            configureTestServices: services =>
            {
                services.RemoveAll<IWeatherSafetyReviewNotifier>();
                services.AddSingleton<IWeatherSafetyReviewNotifier>(notifier);
            });

        using var owningClient = factory.CreateAuthenticatedClient(seed.OperatorId, UserRole.TourOperator);
        using var otherClient = factory.CreateAuthenticatedClient(seed.OtherOperatorId, UserRole.TourOperator);

        // 1. List reviews with status filter
        using var listResponse = await owningClient.GetAsync("/api/v1/operator/weather-safety-reviews?status=PendingAcknowledgement");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var reviews = await listResponse.Content.ReadFromJsonAsync<List<WeatherSafetyReviewDto>>();
        reviews.Should().NotBeNull();
        reviews.Should().HaveCount(1);
        reviews![0].Id.Should().Be(seed.ReviewId);
        reviews[0].Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);
        reviews[0].ScheduleStartAtUtc.Should().Be(Now.AddHours(-1));
        reviews[0].ScheduleEndAtUtc.Should().Be(Now.AddHours(5));
        reviews[0].ScheduleStatus.Should().Be(nameof(TourScheduleStatus.Scheduled));
        reviews[0].EventType.Should().Be("Typhoon");
        reviews[0].Severity.Should().Be(WeatherEvent.SeverityExtreme);
        reviews[0].Description.Should().Be("Extreme typhoon affecting the coastal departure.");
        reviews[0].ValidFromUtc.Should().Be(Now.AddHours(-2));
        reviews[0].ValidToUtc.Should().Be(Now.AddHours(8));
        reviews[0].OriginalAuthority.Should().Be("NCHMF");
        reviews[0].OfficialVietnamRiskLevel.Should().Be(4);
        reviews[0].VerifiedDirective.Should().Be("TravelProhibition");
        reviews[0].AffectedAreaSummary.Should().BeNull();
        reviews[0].IsWithdrawn.Should().BeFalse();

        // An unknown status is a client error, not an empty list.
        using var invalidFilterResponse = await owningClient.GetAsync("/api/v1/operator/weather-safety-reviews?status=Bogus");
        invalidFilterResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var wrongCaseResponse = await owningClient.GetAsync("/api/v1/operator/weather-safety-reviews?status=pendingacknowledgement");
        wrongCaseResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Invariant: Departure remains scheduled, NEVER automatically EmergencyStopped!
        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM commerce.TourSchedules WHERE schedule_id = {seed.ScheduleId};
            """)).Should().Be("Scheduled");

        // 2. Tenant isolation: Other operator cannot access owning operator's review
        using var forbiddenGetResponse = await otherClient.GetAsync($"/api/v1/operator/weather-safety-reviews/{seed.ReviewId}");
        forbiddenGetResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var detailResponse = await owningClient.GetAsync($"/api/v1/operator/weather-safety-reviews/{seed.ReviewId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailDto = await detailResponse.Content.ReadFromJsonAsync<WeatherSafetyReviewDto>();
        detailDto.Should().NotBeNull();
        detailDto!.EventType.Should().Be("Typhoon");
        detailDto.OriginalAuthority.Should().Be("NCHMF");

        using var forbiddenAckResponse = await otherClient.PostAsJsonAsync(
            $"/api/v1/operator/weather-safety-reviews/{seed.ReviewId}/acknowledge",
            new { ImmediateInstructions = "Malicious instructions" });
        forbiddenAckResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 3. Acknowledge review by owning operator
        using var ackResponse = await owningClient.PostAsJsonAsync(
            $"/api/v1/operator/weather-safety-reviews/{seed.ReviewId}/acknowledge",
            new { ImmediateInstructions = "Instructed guide to divert inland away from coast." });
        ackResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var ackDto = await ackResponse.Content.ReadFromJsonAsync<WeatherSafetyReviewDto>();
        ackDto.Should().NotBeNull();
        ackDto!.Status.Should().Be(WeatherSafetyReview.StatusAcknowledged);
        ackDto.ImmediateInstructions.Should().Be("Instructed guide to divert inland away from coast.");
        ackDto.AcknowledgedByOperatorId.Should().Be(seed.OperatorId);
        ackDto.EventType.Should().Be("Typhoon");
        ackDto.ScheduleStatus.Should().Be(nameof(TourScheduleStatus.Scheduled));
        notifier.Calls.Should().ContainSingle(call =>
            call.OperatorUserId == seed.OperatorId
            && call.Notification.ReviewId == seed.ReviewId
            && call.Notification.ChangeType == WeatherSafetyReviewChangeTypes.Updated);

        // Verify SQL Server state
        (await database.ExecuteScalarAsync<string>($"""
            SELECT status FROM trip.WeatherSafetyReviews WHERE review_id = {seed.ReviewId};
            """)).Should().Be(WeatherSafetyReview.StatusAcknowledged);

        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM dbo.AuditLogs
            WHERE action_type = '{AuditActionTypes.WeatherSafetyReviewAcknowledged}'
              AND affected_entity_id = {seed.ReviewId}
              AND actor_user_id = {seed.OperatorId};
            """)).Should().Be(1);

        // 4. Replay acknowledge: Idempotent 200 OK without side effects
        using var replayAckResponse = await owningClient.PostAsJsonAsync(
            $"/api/v1/operator/weather-safety-reviews/{seed.ReviewId}/acknowledge",
            new { ImmediateInstructions = "Instructed guide to divert inland away from coast." });
        replayAckResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        notifier.Calls.Should().HaveCount(1, "idempotent acknowledge replay must not emit another hint");

        // 5. Missed SLA escalation test with an unacknowledged review
        await using (var db = database.CreateDbContext())
        {
            var expiredReview = WeatherSafetyReview.CreateHold(
                seed.ScheduleId,
                "TomorrowIo",
                "evt-escalate-1",
                Now.AddMinutes(-40),
                Now.AddMinutes(-10)); // Deadline was 10 minutes ago
            db.WeatherSafetyReviews.Add(expiredReview);
            await db.SaveChangesAsync();

            var escalationService = new WeatherSafetyReviewEscalationBackgroundService(
                factory.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WeatherSafetyReviewEscalationBackgroundService>.Instance);

            int escalated = await escalationService.ProcessEscalationsAsync(CancellationToken.None);
            escalated.Should().Be(1);
            notifier.Calls.Should().ContainSingle(call =>
                call.Notification.ReviewId == expiredReview.Id
                && call.Notification.ChangeType == WeatherSafetyReviewChangeTypes.Escalated);

            (await database.ExecuteScalarAsync<string>($"""
                SELECT status FROM trip.WeatherSafetyReviews WHERE review_id = {expiredReview.Id};
                """)).Should().Be(WeatherSafetyReview.StatusEscalated);
            (await database.ExecuteScalarAsync<string>($"""
                SELECT escalation_contact FROM trip.WeatherSafetyReviews WHERE review_id = {expiredReview.Id};
                """)).Should().Be("Bộ phận điều phối vận hành");
            using var escalatedResponse = await owningClient.GetAsync($"/api/v1/operator/weather-safety-reviews/{expiredReview.Id}");
            (await escalatedResponse.Content.ReadFromJsonAsync<WeatherSafetyReviewDto>())!.EscalationContact
                .Should().Be("Bộ phận điều phối vận hành");

            (await database.ExecuteScalarAsync<int>($"""
                SELECT COUNT(*) FROM dbo.AuditLogs
                WHERE action_type = '{AuditActionTypes.WeatherSafetyReviewEscalated}'
                  AND affected_entity_id = {expiredReview.Id};
                """)).Should().Be(1);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LocalRealtime_TomorrowAlert_ToWeatherSafetyReview_PreservesManualOnlyInvariants()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using (var db = database.CreateDbContext())
        {
            var hasher = new PasswordHasherService();

            var opUser = new User
            {
                Email = $"op-realtime-{Guid.NewGuid():N}@test.local",
                FullName = "Realtime Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
                PasswordHash = hasher.Hash("Pass123!"),
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
            };
            var otherOpUser = new User
            {
                Email = $"other-realtime-{Guid.NewGuid():N}@test.local",
                FullName = "Other Realtime Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
                PasswordHash = hasher.Hash("Pass123!"),
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
            };
            var travelerUser = new User
            {
                Email = $"traveler-realtime-{Guid.NewGuid():N}@test.local",
                FullName = "Realtime Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                PasswordHash = hasher.Hash("Pass123!"),
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
            };
            db.Users.AddRange(opUser, otherOpUser, travelerUser);
            await db.SaveChangesAsync();

            db.OperatorProfiles.AddRange(
                new OperatorProfile
                {
                    UserId = opUser.Id,
                    CompanyName = "Realtime Tours",
                    TaxCode = "RT-TAX-1",
                    BusinessLicenseNo = "RT-LIC-1",
                    ApprovalStatus = OperatorApprovalStatus.Approved,
                    CreatedAtUtc = Now,
                    UpdatedAtUtc = Now,
                },
                new OperatorProfile
                {
                    UserId = otherOpUser.Id,
                    CompanyName = "Other Realtime Tours",
                    TaxCode = "RT-TAX-2",
                    BusinessLicenseNo = "RT-LIC-2",
                    ApprovalStatus = OperatorApprovalStatus.Approved,
                    CreatedAtUtc = Now,
                    UpdatedAtUtc = Now,
                });

            var category = PoiCategory.Create("Sightseeing", null);
            db.PoiCategories.Add(category);
            await db.SaveChangesAsync();

            var daNangPoi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
            Set(daNangPoi, nameof(PointOfInterest.Name), "Son Tra Peninsula");
            Set(daNangPoi, nameof(PointOfInterest.Latitude), 16.10m);
            Set(daNangPoi, nameof(PointOfInterest.Longitude), 108.25m);
            Set(daNangPoi, nameof(PointOfInterest.CategoryId), category.Id);
            db.PointsOfInterest.Add(daNangPoi);
            await db.SaveChangesAsync();

            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), opUser.Id);
            Set(tour, nameof(Tour.Title), "Da Nang Coastal Storm Tour");
            Set(tour, nameof(Tour.BasePrice), 200_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), TourStatus.Published);
            Set(tour, nameof(Tour.CreatedAtUtc), Now);
            Set(tour, nameof(Tour.UpdatedAtUtc), Now);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();

            var itineraryItem = TourItineraryItem.Create(
                dayNo: 1,
                sequenceNo: 1,
                poiId: daNangPoi.Id,
                plannedTime: null,
                stayDurationMinutes: 60,
                notes: "Son Tra coastal stop",
                isRequiredForWeather: true);
            Set(itineraryItem, nameof(TourItineraryItem.TourId), tour.Id);
            db.TourItineraryItems.Add(itineraryItem);

            // The weather-required stop is a Tomorrow.io Location linked to the configured Alert.
            db.WeatherLocationMappings.Add(WeatherLocationMapping.Create(
                $"poi-{daNangPoi.Id}", StopLocationId, 16.10m, 108.25m, "Son Tra Peninsula", Now.AddHours(8), Now.AddDays(-1)));

            var schedule = TourSchedule.Create(
                startAtUtc: Now.AddHours(-1),
                endAtUtc: Now.AddHours(8),
                meetingPoint: "Da Nang Port",
                totalCapacity: 15,
                createdAtUtc: Now.AddDays(-1),
                adultPrice: 200_000m,
                meetingLatitude: 16.05m,
                meetingLongitude: 108.20m);
            Set(schedule, nameof(TourSchedule.TourId), tour.Id);
            db.TourSchedules.Add(schedule);
            await db.SaveChangesAsync();

            var booking = Booking.Create(
                travelerUser.Id, schedule.Id, 1, 200_000m,
                BookingStatus.Confirmed, PaymentStatus.Paid);
            Set(booking, nameof(Booking.BookingCode), $"BK-{Guid.NewGuid():N}"[..20]);
            Set(booking, nameof(Booking.TourSchedule), schedule);
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();

            var payment = PaymentTransaction.Create(booking.Id, PaymentGateway.PayOS, 200_000m, 2001, Now);
            payment.MarkSuccess($"paid-{booking.Id}-2001", null, Now, 0m);
            db.PaymentTransactions.Add(payment);
            await db.SaveChangesAsync();
        }

        const string webhookSecret = "local-test-secret-key-32bytes-long!";
        var realtimeNotifier = new RecordingWeatherSafetyReviewNotifier();
        using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            dateTimeProviderFactory: _ => new FixedDateTimeProvider(Now),
            configureTestServices: services =>
            {
                services.RemoveAll<IWeatherSafetyReviewNotifier>();
                services.AddSingleton<IWeatherSafetyReviewNotifier>(realtimeNotifier);
                services.Configure<TomorrowIoOptions>(opts =>
                {
                    opts.WebhookSecret = webhookSecret;
                    opts.Enabled = true;
                    opts.ApiKey = "test-api-key-not-real";
                    opts.AlertId = AlertId;
                    opts.WebhookToleranceSeconds = 300;
                    opts.MaxPayloadSizeBytes = 1_048_576;
                });
            });

        using var anonClient = factory.CreateClient();
        long operatorId = await database.ExecuteScalarAsync<long>("SELECT TOP(1) user_id FROM dbo.Users WHERE full_name = 'Realtime Operator';");
        long otherOperatorId = await database.ExecuteScalarAsync<long>("SELECT TOP(1) user_id FROM dbo.Users WHERE full_name = 'Other Realtime Operator';");
        long scheduleId = await database.ExecuteScalarAsync<long>("SELECT TOP(1) schedule_id FROM commerce.TourSchedules;");
        long bookingId = await database.ExecuteScalarAsync<long>("SELECT TOP(1) booking_id FROM commerce.Bookings;");

        using var owningClient = factory.CreateAuthenticatedClient(operatorId, UserRole.TourOperator);
        using var otherClient = factory.CreateAuthenticatedClient(otherOperatorId, UserRole.TourOperator);

        // --- STEP 1: Negative Ingress Cases ---
        using var nonJsonReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent("plain text", Encoding.UTF8, "text/plain")
        };
        using var nonJsonRes = await anonClient.SendAsync(nonJsonReq);
        nonJsonRes.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);

        using var missingSigReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        using var missingSigRes = await anonClient.SendAsync(missingSigReq);
        missingSigRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var invalidSigReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        invalidSigReq.Headers.Add("X-Signature", "t=1730534400,sig=deadbeef00112233445566778899aabbccddeeff");
        using var invalidSigRes = await anonClient.SendAsync(invalidSigReq);
        invalidSigRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string expiredSig = TomorrowAlertFixtures.SignatureHeader(webhookSecret, DateTimeOffset.UtcNow.AddMinutes(-10));
        using var expiredSigReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        expiredSigReq.Headers.Add("X-Signature", expiredSig);
        using var expiredSigRes = await anonClient.SendAsync(expiredSigReq);
        expiredSigRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // --- STEP 2: Pre-Webhook Snapshot of Commercial State ---
        string preScheduleStatus = await database.ExecuteScalarAsync<string>($"SELECT status FROM commerce.TourSchedules WHERE schedule_id = {scheduleId};");
        string preBookingStatus = await database.ExecuteScalarAsync<string>($"SELECT status FROM commerce.Bookings WHERE booking_id = {bookingId};");
        int preOpCount = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations;");
        int preRefundCount = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payment.Refunds;");
        int preOutboxTableCount = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'RefundRequestedOutboxItems';");

        preScheduleStatus.Should().Be("Scheduled");
        preBookingStatus.Should().Be("Confirmed");
        preOpCount.Should().Be(0);
        preRefundCount.Should().Be(0);
        preOutboxTableCount.Should().Be(0);

        // --- STEP 3: The documented Custom Insight webhook for the monitored meeting point ---
        string payload = TomorrowAlertFixtures.Webhook("PRIOR", alertId: AlertId, locationId: StopLocationId,
            startTime: Now.AddMinutes(30));
        string validSig = TomorrowAlertFixtures.SignatureHeader(webhookSecret, DateTimeOffset.UtcNow);

        using var validReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        validReq.Headers.Add("X-Signature", validSig);
        using var validRes = await anonClient.SendAsync(validReq);

        validRes.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var res1Json = await validRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(res1Json))
        {
            doc.RootElement.GetProperty("isDuplicate").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("status").GetString().Should().Be("Pending");
        }

        // Tomorrow.io re-delivers the same body after a failed attempt.
        using var dupReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        dupReq.Headers.Add("X-Signature", validSig);
        using var dupRes = await anonClient.SendAsync(dupReq);

        dupRes.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var res2Json = await dupRes.Content.ReadAsStringAsync();
        using (var doc = JsonDocument.Parse(res2Json))
        {
            doc.RootElement.GetProperty("isDuplicate").GetBoolean().Should().BeTrue();
        }

        int inboxCount = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM trip.WeatherAlertInboxItems;");
        inboxCount.Should().Be(1);

        // --- STEP 4: Lease and process -> WeatherEvent -> WeatherSafetyReview ---
        await LeaseAndProcessAsync(factory);

        (await database.ExecuteScalarAsync<string>("SELECT status FROM trip.WeatherAlertInboxItems;")).Should().Be("Processed");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM trip.WeatherAlertInboxItems WHERE processed_at IS NOT NULL AND last_error IS NULL;")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM trip.WeatherEvents WHERE provider = 'TomorrowIo' AND provider_event_id LIKE '{AlertId}:%:{StopLocationId}';")).Should().Be(1);

        long reviewId = await database.ExecuteScalarAsync<long>($"SELECT review_id FROM trip.WeatherSafetyReviews WHERE tour_schedule_id = {scheduleId};");
        reviewId.Should().BeGreaterThan(0);
        (await database.ExecuteScalarAsync<string>($"SELECT immediate_instructions FROM trip.WeatherSafetyReviews WHERE review_id = {reviewId};"))
            .Should().Contain("Operator review", "a Custom Insight is advisory evidence for the Operator");
        realtimeNotifier.Calls.Should().ContainSingle(call =>
            call.OperatorUserId == operatorId
            && call.Notification.ReviewId == reviewId
            && call.Notification.ChangeType == WeatherSafetyReviewChangeTypes.Created);

        // The review's push notice committed with it, and carries no tour detail.
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM dbo.Notifications notification
            JOIN trip.AlertDeliveryWork work ON work.notification_id = notification.notification_id
            WHERE notification.type = '{Notification.WeatherSafetyReviewCreatedType}'
              AND notification.related_entity_id = {reviewId}
              AND notification.title = N'{Notification.SafetyAlertTitle}'
              AND notification.body NOT LIKE '%Da Nang%';
            """)).Should().Be(1);

        // START of the same occurrence after the Operator acknowledged: still one review, one notice.
        (await database.ExecuteScalarAsync<int>($"""
            UPDATE trip.WeatherSafetyReviews SET status = '{WeatherSafetyReview.StatusAcknowledged}'
            WHERE review_id = {reviewId};
            SELECT @@ROWCOUNT;
            """)).Should().Be(1);
        string startPayload = TomorrowAlertFixtures.Webhook("START", alertId: AlertId, locationId: StopLocationId,
            startTime: Now.AddMinutes(30));
        using (var startReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/tomorrow/alerts")
        {
            Content = new StringContent(startPayload, Encoding.UTF8, "application/json")
        })
        {
            startReq.Headers.Add("X-Signature", TomorrowAlertFixtures.SignatureHeader(webhookSecret, DateTimeOffset.UtcNow));
            using var startRes = await anonClient.SendAsync(startReq);
            startRes.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        await LeaseAndProcessAsync(factory);

        (await database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM trip.WeatherSafetyReviews WHERE tour_schedule_id = {scheduleId};")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM trip.WeatherEvents WHERE provider = 'TomorrowIo';")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM dbo.Notifications WHERE type = '{Notification.WeatherSafetyReviewCreatedType}';")).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM trip.WeatherAlertInboxItems WHERE status = 'Processed';")).Should().Be(2);
        await database.ExecuteScalarAsync<int>($"""
            UPDATE trip.WeatherSafetyReviews SET status = '{WeatherSafetyReview.StatusPendingAcknowledgement}'
            WHERE review_id = {reviewId};
            SELECT @@ROWCOUNT;
            """);

        // --- STEP 5: MANUAL-ONLY INVARIANT AUDIT ---
        string postScheduleStatus = await database.ExecuteScalarAsync<string>($"SELECT status FROM commerce.TourSchedules WHERE schedule_id = {scheduleId};");
        string postBookingStatus = await database.ExecuteScalarAsync<string>($"SELECT status FROM commerce.Bookings WHERE booking_id = {bookingId};");
        int postOpCount = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations;");
        int postRefundCount = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payment.Refunds;");
        int postOutboxTableCount = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'RefundRequestedOutboxItems';");

        postScheduleStatus.Should().Be("Scheduled", "Weather MUST NEVER mutate TourSchedule status to EmergencyStopped or Cancelled!");
        postBookingStatus.Should().Be("Confirmed", "Weather MUST NEVER mutate Booking status to Interrupted or Cancelled!");
        postOpCount.Should().Be(0, "Weather MUST NEVER create EmergencyTourCancellationOperations!");
        postRefundCount.Should().Be(0, "Weather MUST NEVER create Refunds!");
        postOutboxTableCount.Should().Be(0, "payment.RefundRequestedOutboxItems MUST remain retired and absent!");

        // --- STEP 6: Operator Review Lifecycle & Tenant Isolation ---
        using var listRes = await owningClient.GetAsync("/api/v1/operator/weather-safety-reviews?status=PendingAcknowledgement");
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var reviewList = await listRes.Content.ReadFromJsonAsync<List<WeatherSafetyReviewDto>>();
        reviewList.Should().NotBeNull();
        reviewList!.Should().ContainSingle(r => r.Id == reviewId);

        using var getRes = await owningClient.GetAsync($"/api/v1/operator/weather-safety-reviews/{reviewId}");
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var reviewDto = await getRes.Content.ReadFromJsonAsync<WeatherSafetyReviewDto>();
        reviewDto!.Status.Should().Be(WeatherSafetyReview.StatusPendingAcknowledgement);

        using var otherGetRes = await otherClient.GetAsync($"/api/v1/operator/weather-safety-reviews/{reviewId}");
        otherGetRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var otherAckRes = await otherClient.PostAsJsonAsync(
            $"/api/v1/operator/weather-safety-reviews/{reviewId}/acknowledge",
            new { ImmediateInstructions = "Malicious instructions" });
        otherAckRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var ackRes = await owningClient.PostAsJsonAsync(
            $"/api/v1/operator/weather-safety-reviews/{reviewId}/acknowledge",
            new { ImmediateInstructions = "Divert route inland, avoid Son Tra coastal pass." });
        ackRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var ackDto = await ackRes.Content.ReadFromJsonAsync<WeatherSafetyReviewDto>();
        ackDto!.Status.Should().Be(WeatherSafetyReview.StatusAcknowledged);
        ackDto.ImmediateInstructions.Should().Be("Divert route inland, avoid Son Tra coastal pass.");
        ackDto.AcknowledgedByOperatorId.Should().Be(operatorId);
        realtimeNotifier.Calls.Should().ContainSingle(call =>
            call.OperatorUserId == operatorId
            && call.Notification.ReviewId == reviewId
            && call.Notification.ChangeType == WeatherSafetyReviewChangeTypes.Updated);

        (await database.ExecuteScalarAsync<string>($"SELECT status FROM trip.WeatherSafetyReviews WHERE review_id = {reviewId};"))
            .Should().Be(WeatherSafetyReview.StatusAcknowledged);
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM dbo.AuditLogs
            WHERE action_type = '{AuditActionTypes.WeatherSafetyReviewAcknowledged}'
              AND affected_entity_id = {reviewId}
              AND actor_user_id = {operatorId};
            """)).Should().Be(1);

        using var replayAckRes = await owningClient.PostAsJsonAsync(
            $"/api/v1/operator/weather-safety-reviews/{reviewId}/acknowledge",
            new { ImmediateInstructions = "Divert route inland, avoid Son Tra coastal pass." });
        replayAckRes.StatusCode.Should().Be(HttpStatusCode.OK);

        (await database.ExecuteScalarAsync<string>($"SELECT status FROM commerce.TourSchedules WHERE schedule_id = {scheduleId};"))
            .Should().Be("Scheduled");
        (await database.ExecuteScalarAsync<string>($"SELECT status FROM commerce.Bookings WHERE booking_id = {bookingId};"))
            .Should().Be("Confirmed");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commerce.EmergencyTourCancellationOperations;"))
            .Should().Be(0);

        // --- STEP 7: SLA Escalation ---
        await using (var db = database.CreateDbContext())
        {
            var expiredReview = WeatherSafetyReview.CreateHold(
                scheduleId,
                "TomorrowIo",
                "evt-overdue-999",
                Now.AddMinutes(-45),
                Now.AddMinutes(-15));
            db.WeatherSafetyReviews.Add(expiredReview);
            await db.SaveChangesAsync();

            var escalationService = new WeatherSafetyReviewEscalationBackgroundService(
                factory.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WeatherSafetyReviewEscalationBackgroundService>.Instance);

            int escalated = await escalationService.ProcessEscalationsAsync(CancellationToken.None);
            escalated.Should().Be(1);

            (await database.ExecuteScalarAsync<string>($"SELECT status FROM trip.WeatherSafetyReviews WHERE review_id = {expiredReview.Id};"))
                .Should().Be(WeatherSafetyReview.StatusEscalated);
            (await database.ExecuteScalarAsync<int>($"""
                SELECT COUNT(*) FROM dbo.AuditLogs
                WHERE action_type = '{AuditActionTypes.WeatherSafetyReviewEscalated}'
                  AND affected_entity_id = {expiredReview.Id};
                """)).Should().Be(1);
        }
    }

    private sealed record SeedResult(long OperatorId, long OtherOperatorId, long TourId, long ScheduleId, long ReviewId);

    private static async Task<SeedResult> SeedAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var hasher = new PasswordHasherService();

        var opUser = new User
        {
            Email = $"op-review-{Guid.NewGuid():N}@test.local",
            FullName = "Review Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            PasswordHash = hasher.Hash("Pass123!"),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var otherOpUser = new User
        {
            Email = $"other-op-{Guid.NewGuid():N}@test.local",
            FullName = "Other Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            PasswordHash = hasher.Hash("Pass123!"),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.AddRange(opUser, otherOpUser);
        await db.SaveChangesAsync();

        db.OperatorProfiles.AddRange(
            new OperatorProfile
            {
                UserId = opUser.Id,
                CompanyName = "Review Tours",
                TaxCode = "REV-TAX",
                BusinessLicenseNo = "REV-LIC",
                ApprovalStatus = OperatorApprovalStatus.Approved,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
            },
            new OperatorProfile
            {
                UserId = otherOpUser.Id,
                CompanyName = "Other Tours",
                TaxCode = "OTH-TAX",
                BusinessLicenseNo = "OTH-LIC",
                ApprovalStatus = OperatorApprovalStatus.Approved,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
            });

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), opUser.Id);
        Set(tour, nameof(Tour.Title), "Active Coastal Excursion");
        Set(tour, nameof(Tour.BasePrice), 200_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        Set(tour, nameof(Tour.CreatedAtUtc), Now);
        Set(tour, nameof(Tour.UpdatedAtUtc), Now);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        // Active departure: started 1 hour ago, ends in 5 hours
        var schedule = TourSchedule.Create(
            startAtUtc: Now.AddHours(-1),
            endAtUtc: Now.AddHours(5),
            meetingPoint: "Son Tra Coast",
            totalCapacity: 15,
            createdAtUtc: Now.AddDays(-1),
            adultPrice: 200_000m);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var weatherEvent = WeatherEvent.CreateOfficial(
            provider: "TomorrowIo",
            providerEventId: "evt-hold-1",
            eventType: "Typhoon",
            severity: WeatherEvent.SeverityExtreme,
            validFromUtc: Now.AddHours(-2),
            validToUtc: Now.AddHours(8),
            description: "Extreme typhoon affecting the coastal departure.",
            originalAuthority: "NCHMF",
            officialVietnamRiskLevel: 4,
            verifiedDirective: "TravelProhibition",
            ingestedAtUtc: Now.AddMinutes(-5));
        db.WeatherEvents.Add(weatherEvent);
        await db.SaveChangesAsync();

        // Safety hold review with deadline in 30 minutes
        var review = WeatherSafetyReview.CreateHold(
            schedule.Id,
            "TomorrowIo",
            "evt-hold-1",
            urgentNoticeSentAtUtc: Now,
            acknowledgementDeadlineUtc: Now.AddMinutes(30),
            immediateInstructions: "Monitor maritime weather bulletin.");
        db.WeatherSafetyReviews.Add(review);
        await db.SaveChangesAsync();

        return new SeedResult(opUser.Id, otherOpUser.Id, tour.Id, schedule.Id, review.Id);
    }

    private static void Set(object target, string propertyName, object? value)
    {
        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (prop != null)
        {
            prop.SetValue(target, value);
            return;
        }

        var field = target.GetType().GetField($"_{char.ToLowerInvariant(propertyName[0])}{propertyName[1..]}", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? target.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(target, value);
    }
}

using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.CommercialServices.Booking;
using TripMate.Application.Features.Notifications.GetInbox;
using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.ServiceProviderRequests;

[Collection(nameof(TripMateApiFactory))]
[Trait("Category", "SqlServer")]
public sealed class ServiceProviderRequestSqlServerTests
{
    [SqlServerFact]
    public async Task TravelerCanCancelOwnPendingRequestOnceAndProviderCannotConfirmIt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var outsider = new User
        {
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            FullName = "Another Traveler",
            Email = "another-traveler@example.test",
            CreatedAtUtc = fixture.Now,
            UpdatedAtUtc = fixture.Now
        };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();
        await using var api = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString,
            dateTimeProviderFactory: _ => new FixedClock(fixture.Now));
        using var traveler = api.CreateAuthenticatedClient(fixture.TravelerId, UserRole.Traveler);
        using var otherTraveler = api.CreateAuthenticatedClient(outsider.Id, UserRole.Traveler);
        using var provider = api.CreateAuthenticatedClient(fixture.ActorId, UserRole.ServiceProvider);
        const string requestsPath = "/api/v1/traveler/service-booking-requests";
        var requests = await traveler.GetFromJsonAsync<TravelerServiceBooking[]>(requestsPath);
        requests.Should().ContainSingle(item => item.Id == fixture.BookingId && item.Status == ServiceBooking.PendingConfirmation);
        var cancellationPath = $"{requestsPath}/{fixture.BookingId}/cancel";
        (await provider.PostAsync(cancellationPath, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await otherTraveler.PostAsync(cancellationPath, null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherTraveler.GetFromJsonAsync<TravelerServiceBooking[]>(requestsPath)).Should().BeEmpty();
        (await traveler.PostAsync(cancellationPath, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await traveler.PostAsync(cancellationPath, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        requests = await traveler.GetFromJsonAsync<TravelerServiceBooking[]>(requestsPath);
        requests.Should().ContainSingle(item => item.Id == fixture.BookingId && item.Status == ServiceBooking.Cancelled);
        var inbox = await traveler.GetFromJsonAsync<GetNotificationInboxResponse>("/api/v1/notifications");
        inbox!.Items.Should().ContainSingle(item => item.RelatedEntityId == fixture.BookingId && item.Body!.Contains("MSG186"));
        var detailPath = $"/api/v1/provider/service-booking-requests/{fixture.BookingId}";
        var detail = await provider.GetFromJsonAsync<ProviderRequestDetail>(detailPath);
        provider.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await provider.PostAsJsonAsync($"{detailPath}/decision", new { decision = ServiceBooking.Confirmed, detail!.RowVersion }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [SqlServerFact]
    public async Task ProviderPublishesAvailabilityAndTravelerSubmitsOneNotifiedRequest()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var original = await db.ServiceBookings.SingleAsync();
        await using var api = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString,
            dateTimeProviderFactory: _ => new FixedClock(fixture.Now));
        using var provider = api.CreateAuthenticatedClient(fixture.ActorId, UserRole.ServiceProvider);
        using var outsider = api.CreateAuthenticatedClient(fixture.OtherProviderId, UserRole.ServiceProvider);
        using var traveler = api.CreateAuthenticatedClient(original.TravelerUserId, UserRole.Traveler);
        var start = fixture.Now.AddDays(2).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var availability = $"/api/v1/provider/commercial-services/{original.ServiceId}/availability/{start}";
        (await outsider.PutAsJsonAsync(availability, new { quantity = 2 })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await provider.PutAsJsonAsync(availability, new { quantity = 2 })).StatusCode.Should().Be(HttpStatusCode.OK);
        var published = await traveler.GetAsync($"/api/v1/commercial-services/{original.ServiceId}/availability");
        published.StatusCode.Should().Be(HttpStatusCode.OK);
        (await published.Content.ReadAsStringAsync()).Should().Contain("\"remainingQuantity\":2");

        const string submit = "/api/v1/traveler/service-booking-requests";
        traveler.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var request = new
        {
            serviceId = original.ServiceId,
            startsAt = start,
            quantity = 1,
            contactName = "Booking contact",
            contactPhone = "0901234567",
            contactEmail = "contact@example.test"
        };
        var past = await traveler.PostAsJsonAsync(submit, request with
        {
            startsAt = fixture.Now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ")
        });
        past.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await past.Content.ReadAsStringAsync()).Should().Contain("MSG76");
        var closed = await traveler.PostAsJsonAsync(submit, request with { serviceId = original.ServiceId + 100 });
        closed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await closed.Content.ReadAsStringAsync()).Should().Contain("MSG75");
        var created = await traveler.PostAsJsonAsync(submit, request);
        var retry = await traveler.PostAsJsonAsync(submit, request);
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        (await retry.Content.ReadAsStringAsync()).Should().Be(await created.Content.ReadAsStringAsync());
        (await traveler.PostAsJsonAsync(submit, request with { quantity = 2 })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var booking = await db.ServiceBookings.AsNoTracking().SingleAsync(item => item.Id != fixture.BookingId);
        booking.TotalPrice.Should().Be(original.TotalPrice);
        (await db.Notifications.CountAsync(item => item.UserId == fixture.ActorId
            && item.Type == "MSG205" && item.RelatedEntityId == booking.Id)).Should().Be(1);
        var detail = await provider.GetFromJsonAsync<ProviderRequestDetail>(
            $"/api/v1/provider/service-booking-requests/{booking.Id}");
        detail!.TravelerEmail.Should().Be("contact@example.test");
        traveler.DefaultRequestHeaders.Remove("Idempotency-Key");
        traveler.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await traveler.PostAsJsonAsync(submit, request with { quantity = 2 })).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await db.ServiceBookings.AsNoTracking().SingleAsync(item => item.Id != fixture.BookingId && item.Id != booking.Id);
        var secondPath = $"/api/v1/provider/service-booking-requests/{second.Id}";
        var secondDetail = await provider.GetFromJsonAsync<ProviderRequestDetail>(secondPath);
        using var firstDecision = Confirm(booking.Id, detail.RowVersion);
        using var secondDecision = Confirm(second.Id, secondDetail!.RowVersion);
        var decisions = await Task.WhenAll(provider.SendAsync(firstDecision), provider.SendAsync(secondDecision));
        decisions.Select(response => response.StatusCode).Should().BeEquivalentTo(
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        var confirmedId = decisions[0].StatusCode == HttpStatusCode.OK ? booking.Id : second.Id;
        var travelerInbox = await traveler.GetFromJsonAsync<GetNotificationInboxResponse>("/api/v1/notifications");
        travelerInbox!.Items.Should().ContainSingle(item => item.Type == ProviderRequestEvents.NotificationType
            && item.RelatedEntityId == confirmedId && item.Body!.Contains("MSG72"));

        static HttpRequestMessage Confirm(long bookingId, string rowVersion)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/provider/service-booking-requests/{bookingId}/decision")
            {
                Content = JsonContent.Create(new { decision = ServiceBooking.Confirmed, rowVersion })
            };
            message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return message;
        }
    }

    [SqlServerFact]
    public async Task New_UC31_booking_sends_MSG205_to_the_linked_provider_only()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var booking = await db.ServiceBookings.SingleAsync();
        await using var api = new TripMateApiFactory(sqlServerConnectionString: database.ConnectionString);
        using var provider = api.CreateAuthenticatedClient(fixture.ActorId, UserRole.ServiceProvider);
        using var otherProvider = api.CreateAuthenticatedClient(fixture.OtherProviderId, UserRole.ServiceProvider);
        using var traveler = api.CreateAuthenticatedClient(booking.TravelerUserId, UserRole.Traveler);

        const string inboxPath = "/api/v1/notifications";
        var providerInbox = await provider.GetFromJsonAsync<GetNotificationInboxResponse>($"{inboxPath}?unreadOnly=true");
        var notification = providerInbox!.Items.Should().ContainSingle().Which;
        notification.Type.Should().Be("MSG205");
        notification.RelatedEntityType.Should().Be(nameof(ServiceBooking));
        notification.RelatedEntityId.Should().Be(fixture.BookingId);
        notification.Body.Should().Contain($"SR-{fixture.BookingId}").And.Contain("2026-11-15");
        notification.IsRead.Should().BeFalse();

        (await traveler.GetFromJsonAsync<GetNotificationInboxResponse>(inboxPath))!.Items.Should().BeEmpty();
        (await otherProvider.GetFromJsonAsync<GetNotificationInboxResponse>(inboxPath))!.Items.Should().BeEmpty();
        var readPath = $"{inboxPath}/{notification.Id}/read";
        (await otherProvider.PostAsync(readPath, null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await provider.PostAsync(readPath, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await provider.GetFromJsonAsync<GetNotificationInboxResponse>($"{inboxPath}?unreadOnly=true"))!
            .Items.Should().BeEmpty();

        var requestPath = $"/api/v1/provider/service-booking-requests/{fixture.BookingId}";
        var detail = await provider.GetFromJsonAsync<ProviderRequestDetail>(requestPath);
        detail!.Id.Should().Be(notification.RelatedEntityId);
        (await otherProvider.GetAsync(requestPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await db.Notifications.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    public async Task MSG205_rolls_back_with_an_uncommitted_new_request_and_migration_can_run_twice()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var migration = Path.Combine(SqlServerTestDatabase.DefaultMigrationsDirectory(),
            "20261125_uc31_provider_booking_notice.sql");
        await database.ExecuteScriptAsync(migration);
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var existing = await db.ServiceBookings.SingleAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var request = ServiceBooking.CreatePendingRequest(existing.TravelerUserId, existing.ServiceId,
                existing.StartAtUtc.AddDays(1), null, 1, existing.TotalPrice, fixture.Now);
            db.ServiceBookings.Add(request);
            await db.SaveChangesAsync();
            (await db.Notifications.CountAsync(item => item.Type == "MSG205")).Should().Be(2);
            await transaction.RollbackAsync();
        }

        (await db.ServiceBookings.CountAsync()).Should().Be(1);
        (await db.Notifications.CountAsync(item => item.Type == "MSG205")).Should().Be(1);
    }

    [SqlServerFact]
    public async Task SQL_list_and_detail_project_persisted_option_contacts_and_Vietnam_requested_date()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var current = new CurrentProvider(fixture.ActorId);
        var page = await new ListServiceProviderRequestsHandler(db, current).Handle(new ListServiceProviderRequests(
            Keyword: "Double room", RequestedDateFrom: new DateOnly(2026, 11, 15),
            RequestedDateTo: new DateOnly(2026, 11, 15)), default);
        page.IsSuccess.Should().BeTrue();
        page.Value.TotalCount.Should().Be(1);
        page.Value.Items.Single().ServiceName.Should().Be("Legacy hotel room");
        var detail = await new GetServiceProviderRequestHandler(db, current)
            .Handle(new GetServiceProviderRequest(fixture.BookingId), default);
        detail.Value.Option.Should().Be("Double room");
        detail.Value.SpecialRequest.Should().Be("Late arrival");
        detail.Value.TravelerEmail.Should().Be("traveler-request@example.test");
    }

    [SqlServerFact]
    public async Task SQL_list_orders_pending_by_earliest_date_then_unique_id_across_pages()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var original = await db.ServiceBookings.SingleAsync();
        var sameDate = ServiceBooking.CreatePendingRequest(original.TravelerUserId, original.ServiceId,
            original.StartAtUtc, null, 1, 1000, fixture.Now);
        var later = ServiceBooking.CreatePendingRequest(original.TravelerUserId, original.ServiceId,
            original.StartAtUtc.AddDays(1), null, 1, 1000, fixture.Now);
        var earlierConfirmed = ServiceBooking.CreatePendingRequest(original.TravelerUserId, original.ServiceId,
            original.StartAtUtc.AddDays(-1), null, 1, 1000, fixture.Now);
        earlierConfirmed.TryDecide(ServiceBooking.Confirmed, null, fixture.ActorId, fixture.Now);
        db.ServiceBookings.AddRange(sameDate, later, earlierConfirmed);
        await db.SaveChangesAsync();
        var handler = new ListServiceProviderRequestsHandler(db, new CurrentProvider(fixture.ActorId));
        var first = await handler.Handle(new ListServiceProviderRequests(PageSize: 2), default);
        var second = await handler.Handle(new ListServiceProviderRequests(PageNumber: 2, PageSize: 2), default);
        first.Value.TotalCount.Should().Be(4);
        first.Value.Items.Select(item => item.Id).Should().Equal(original.Id, sameDate.Id);
        second.Value.Items.Select(item => item.Id).Should().Equal(later.Id, earlierConfirmed.Id);
    }

    [SqlServerFact]
    public async Task Rejection_persists_full_reason_and_notifies_traveler_without_commission()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var reason = new string('R', ServiceBooking.RejectionReasonMaxLength);
        var command = new DecideServiceProviderRequest(fixture.BookingId, ServiceBooking.Rejected,
            reason, fixture.RowVersion, Guid.NewGuid());
        var response = await DecisionHandler(db, fixture).Handle(command, default);
        response.IsSuccess.Should().BeTrue();
        response.Value.RejectionReason.Should().Be(reason);
        response.Value.CommissionAmount.Should().Be(0);
        await AssertEffectsAsync(database, ServiceBooking.Rejected, 1);
        await using var check = database.CreateDbContext();
        (await check.Notifications.SingleAsync(item => item.Type == ProviderRequestEvents.NotificationType))
            .Body.Should().Be(reason);
        var persisted = await check.ServiceBookings.SingleAsync();
        persisted.RespondedByUserId.Should().Be(fixture.ActorId);
        persisted.RespondedAtUtc.Should().Be(fixture.Now);
    }

    [SqlServerTheory]
    [InlineData(ServiceBooking.Completed, 123456.78, 12345)]
    [InlineData(ServiceBooking.NoShow, 0, 0)]
    public async Task Confirmed_request_records_outcome_and_replays_identical_response(string outcome,
        decimal actualAmount, decimal commission)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var confirmed = await DecisionHandler(db, fixture).Handle(new DecideServiceProviderRequest(fixture.BookingId,
            ServiceBooking.Confirmed, null, fixture.RowVersion, Guid.NewGuid()), default);
        confirmed.IsSuccess.Should().BeTrue();
        var clock = new FixedClock(confirmed.Value.OutcomeAvailableAtUtc);
        var handler = new RecordServiceProviderOutcomeHandler(db, new CurrentProvider(fixture.ActorId),
            new ProviderRequestMutationService(db, new SqlServerServiceProviderRequestLock(db), clock));
        var command = new RecordServiceProviderOutcome(fixture.BookingId, outcome, actualAmount,
            confirmed.Value.RowVersion, Guid.NewGuid());
        var result = await handler.Handle(command, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(outcome);
        result.Value.ActualAmount.Should().Be(actualAmount);
        result.Value.CommissionAmount.Should().Be(commission);
        result.Value.RowVersion.Should().NotBe(confirmed.Value.RowVersion);
        (await handler.Handle(command, default)).Value.Should().BeEquivalentTo(result.Value);
        await AssertEffectsAsync(database, outcome, 2);
    }

    [SqlServerFact]
    public async Task Reused_key_with_different_payload_returns_conflict_without_more_events()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var handler = DecisionHandler(db, fixture);
        var command = new DecideServiceProviderRequest(fixture.BookingId, ServiceBooking.Confirmed,
            null, fixture.RowVersion, Guid.NewGuid());
        (await handler.Handle(command, default)).IsSuccess.Should().BeTrue();
        (await handler.Handle(command with { Decision = ServiceBooking.Rejected, Reason = "Changed" }, default))
            .ErrorCode.Should().Be(ProviderRequestErrors.IdempotencyMismatch);
        await AssertEffectsAsync(database, ServiceBooking.Confirmed, 1);
    }

    [SqlServerFact]
    public async Task Foreign_provider_cannot_read_contacts_or_mutate_request()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var user = new User
        {
            Role = UserRole.ServiceProvider,
            FullName = "Other",
            Status = AccountStatus.Active,
            Email = "other-provider@example.test",
            CreatedAtUtc = fixture.Now,
            UpdatedAtUtc = fixture.Now
        };
        var provider = ServiceProvider.Create("Other provider", "Hotel", "Active");
        provider.AssignAccount(user);
        db.ServiceProviders.Add(provider);
        await db.SaveChangesAsync();
        var current = new CurrentProvider(user.Id);
        var detail = await new GetServiceProviderRequestHandler(db, current)
            .Handle(new GetServiceProviderRequest(fixture.BookingId), default);
        detail.ErrorCode.Should().Be(ProviderRequestErrors.NotFound);
        var command = new DecideServiceProviderRequest(fixture.BookingId, ServiceBooking.Confirmed,
            null, fixture.RowVersion, Guid.NewGuid());
        var result = await new DecideServiceProviderRequestHandler(db, current,
            new ProviderRequestMutationService(db, new SqlServerServiceProviderRequestLock(db), new FixedClock(fixture.Now)))
            .Handle(command, default);
        result.ErrorCode.Should().Be(ProviderRequestErrors.NotFound);
        await AssertEffectsAsync(database, ServiceBooking.PendingConfirmation, 0);
    }

    [SqlServerFact]
    public async Task Concurrent_identical_decisions_replay_once_without_partial_or_duplicate_side_effects()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();
        var command = new DecideServiceProviderRequest(fixture.BookingId, ServiceBooking.Confirmed,
            null, fixture.RowVersion, Guid.NewGuid());
        var results = await Task.WhenAll(DecisionHandler(firstDb, fixture).Handle(command, default),
            DecisionHandler(secondDb, fixture).Handle(command, default));
        results.Should().OnlyContain(result => result.IsSuccess);
        results[0].Value.Should().BeEquivalentTo(results[1].Value);
        await AssertEffectsAsync(database, ServiceBooking.Confirmed, 1);
    }

    [SqlServerFact]
    public async Task Concurrent_different_decisions_have_one_winner_and_one_version_conflict()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();
        var confirm = new DecideServiceProviderRequest(fixture.BookingId, ServiceBooking.Confirmed,
            null, fixture.RowVersion, Guid.NewGuid());
        var reject = confirm with { Decision = ServiceBooking.Rejected, Reason = "Unavailable", IdempotencyKey = Guid.NewGuid() };
        var results = await Task.WhenAll(DecisionHandler(firstDb, fixture).Handle(confirm, default),
            DecisionHandler(secondDb, fixture).Handle(reject, default));
        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Single(result => result.IsFailure).ErrorCode.Should().Be(ProviderRequestErrors.VersionConflict);
        await AssertEffectsAsync(database, results.Single(result => result.IsSuccess).Value.Status, 1);
    }

    [SqlServerFact]
    public async Task Failure_persisting_replay_rolls_back_booking_audit_and_notification_then_retry_succeeds()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        var command = new DecideServiceProviderRequest(fixture.BookingId, ServiceBooking.Confirmed,
            null, fixture.RowVersion, Guid.NewGuid());
        await using (var failingDb = database.CreateDbContext(new RejectOperationSave()))
        {
            var action = () => DecisionHandler(failingDb, fixture).Handle(command, default);
            await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected operation save failure.");
            failingDb.ChangeTracker.Entries().Should().BeEmpty();
        }
        await AssertEffectsAsync(database, ServiceBooking.PendingConfirmation, 0);
        await using var retryDb = database.CreateDbContext();
        (await DecisionHandler(retryDb, fixture).Handle(command, default)).IsSuccess.Should().BeTrue();
        await AssertEffectsAsync(database, ServiceBooking.Confirmed, 1);
    }

    [SqlServerFact]
    public async Task Two_workers_rerun_expiry_once_and_leave_legacy_confirmed_booking_untouched()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using (var seedDb = database.CreateDbContext())
        {
            var request = await seedDb.ServiceBookings.SingleAsync();
            var legacy = new ServiceBooking();
            typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.TravelerUserId))!.SetValue(legacy, request.TravelerUserId);
            typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.ServiceId))!.SetValue(legacy, request.ServiceId);
            typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.StartAtUtc))!.SetValue(legacy, fixture.Now.AddYears(-1));
            typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.Quantity))!.SetValue(legacy, 1);
            typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.Status))!.SetValue(legacy, "Confirmed");
            typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.CreatedAtUtc))!.SetValue(legacy, fixture.Now);
            seedDb.ServiceBookings.Add(legacy);
            await seedDb.SaveChangesAsync();
        }
        var clock = new FixedClock(fixture.Now.AddHours(24));
        await using var firstDb = database.CreateDbContext();
        await using var secondDb = database.CreateDbContext();
        var first = new ServiceRequestLifecycleProcessor(firstDb, new SqlServerServiceProviderRequestLock(firstDb), clock);
        var second = new ServiceRequestLifecycleProcessor(secondDb, new SqlServerServiceProviderRequestLock(secondDb), clock);
        (await Task.WhenAll(first.ProcessAsync(default), second.ProcessAsync(default))).Sum().Should().Be(1);
        (await first.ProcessAsync(default)).Should().Be(0);
        await using var check = database.CreateDbContext();
        (await check.ServiceBookings.SingleAsync(booking => booking.Id == fixture.BookingId)).Status.Should().Be(ServiceBooking.Expired);
        (await check.ServiceBookings.SingleAsync(booking => booking.RequestSubmittedAtUtc == null)).Status.Should().Be("Confirmed");
        (await check.AuditLogs.CountAsync()).Should().Be(1);
        (await check.Notifications.CountAsync(item => item.Type == ProviderRequestEvents.NotificationType)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Automatic_outcome_uses_estimated_amount_at_Vietnam_calendar_deadline_once()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var fixture = await SeedAsync(database);
        await using (var decideDb = database.CreateDbContext())
        {
            var command = new DecideServiceProviderRequest(fixture.BookingId, ServiceBooking.Confirmed,
                null, fixture.RowVersion, Guid.NewGuid());
            (await DecisionHandler(decideDb, fixture).Handle(command, default)).IsSuccess.Should().BeTrue();
        }
        await using var workerDb = database.CreateDbContext();
        var booking = await workerDb.ServiceBookings.AsNoTracking().SingleAsync();
        var worker = new ServiceRequestLifecycleProcessor(workerDb, new SqlServerServiceProviderRequestLock(workerDb),
            new FixedClock(booking.AutoCompleteAtUtc));
        (await worker.ProcessAsync(default)).Should().Be(1);
        (await worker.ProcessAsync(default)).Should().Be(0);
        await using var check = database.CreateDbContext();
        var completed = await check.ServiceBookings.SingleAsync();
        completed.Status.Should().Be(ServiceBooking.Completed);
        completed.ActualAmount.Should().Be(123456.78m);
        completed.CommissionAmount.Should().Be(12345m);
        completed.IsAutoCompleted.Should().BeTrue();
        (await check.AuditLogs.CountAsync()).Should().Be(2);
        (await check.Notifications.CountAsync(item => item.Type == ProviderRequestEvents.NotificationType)).Should().Be(2);
    }

    private static DecideServiceProviderRequestHandler DecisionHandler(ApplicationDbContext db, Seed fixture) =>
        new(db, new CurrentProvider(fixture.ActorId), new ProviderRequestMutationService(db,
            new SqlServerServiceProviderRequestLock(db), new FixedClock(fixture.Now)));

    private static async Task AssertEffectsAsync(SqlServerTestDatabase database, string status, int count)
    {
        await using var db = database.CreateDbContext();
        (await db.ServiceBookings.SingleAsync()).Status.Should().Be(status);
        (await db.AuditLogs.CountAsync()).Should().Be(count);
        (await db.Notifications.CountAsync(item => item.Type == ProviderRequestEvents.NotificationType)).Should().Be(count);
        (await db.ServiceProviderRequestOperations.CountAsync()).Should().Be(count);
    }

    internal static async Task<Seed> SeedAsync(SqlServerTestDatabase database, bool includeBooking = true)
    {
        var now = DateTimeOffset.Parse("2026-11-14T04:00:00Z");
        await using var db = database.CreateDbContext();
        var user = new User
        {
            Role = UserRole.ServiceProvider,
            Status = AccountStatus.Active,
            FullName = "Provider",
            Email = "provider-request@example.test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var traveler = new User
        {
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            FullName = "Traveler",
            Email = "traveler-request@example.test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var otherProvider = new User
        {
            Role = UserRole.ServiceProvider,
            Status = AccountStatus.Active,
            FullName = "Legacy listing owner",
            Email = "legacy-listing-provider@example.test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Users.AddRange(user, traveler, otherProvider);
        await db.SaveChangesAsync();
        var provider = ServiceProvider.Create("Linked provider", "Hotel", ServiceProvider.StatusActive);
        provider.AssignAccount(user);
        var legacy = ServiceProvider.Create("Legacy listing owner", "Hotel", ServiceProvider.StatusActive);
        legacy.AssignAccount(otherProvider);
        var category = await db.PoiCategories.FirstOrDefaultAsync(value => value.Name == "Hotel") ?? PoiCategory.Create("Hotel", null);
        var poi = PointOfInterest.Create(category, "Hotel POI", 16, 108, user.Id, now);
        db.PointsOfInterest.Add(poi);
        db.ServiceProviders.AddRange(provider, legacy);
        await db.SaveChangesAsync();
        db.ProviderPoiLinks.Add(ProviderPoiLink.Create(provider, poi));
        var service = CommercialService.Create(legacy, "Hotel", "Legacy hotel room", 123456.78m,
            CommercialService.PriceUnitPerNight, CommercialService.AvailabilityAvailable, "VND", false,
            null, null, null, now, poiId: poi.Id);
        db.CommercialServices.Add(service);
        await db.SaveChangesAsync();
        var booking = ServiceBooking.CreatePendingRequest(traveler.Id, service.Id, now.AddDays(1), null,
            1, 123456.78m, now, serviceOption: "Double room", specialRequest: "Late arrival");
        if (includeBooking) db.ServiceBookings.Add(booking);
        await db.SaveChangesAsync();
        return new Seed(user.Id, booking.Id, Convert.ToBase64String(booking.RowVersion), now, otherProvider.Id, traveler.Id, service.Id);
    }

    internal sealed record Seed(long ActorId, long BookingId, string RowVersion, DateTimeOffset Now, long OtherProviderId,
        long TravelerId, long ServiceId);
    private sealed class CurrentProvider(long id) : ICurrentUserService
    {
        public long? UserId => id;
        public string? Role => nameof(UserRole.ServiceProvider);
    }
    private sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
    private sealed class RejectOperationSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ServiceProviderRequestOperation>()
                .Any(entry => entry.State == EntityState.Added))
                throw new InvalidOperationException("Injected operation save failure.");
            return ValueTask.FromResult(result);
        }
    }
}
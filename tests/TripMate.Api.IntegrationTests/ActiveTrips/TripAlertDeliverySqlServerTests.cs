using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.TripAlerts;

namespace TripMate.Api.IntegrationTests.ActiveTrips;

public sealed class TripAlertDeliverySqlServerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClaimNextAsync_WithTwoWorkers_ClaimsTheNotificationOnce()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var firstContext = database.CreateDbContext();
        await using var secondContext = database.CreateDbContext();
        var options = Options.Create(new TripAlertOptions());
        var first = new AlertDeliveryQueue(firstContext, new Clock(), options);
        var second = new AlertDeliveryQueue(secondContext, new Clock(), options);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<ClaimedTripAlert?> firstClaim = ClaimAfterGate(first, gate.Task);
        Task<ClaimedTripAlert?> secondClaim = ClaimAfterGate(second, gate.Task);
        gate.SetResult(true);
        ClaimedTripAlert?[] claims = await Task.WhenAll(firstClaim, secondClaim);

        claims.Count(claim => claim is not null).Should().Be(1);
        claims.Single(claim => claim is not null)!.AttemptCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CompleteAsync_AfterClaim_MarksNotificationSentAndReleasesLease()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var queue = new AlertDeliveryQueue(
            context,
            new Clock(),
            Options.Create(new TripAlertOptions()));

        var claim = await queue.ClaimNextAsync(CancellationToken.None);
        claim.Should().NotBeNull();

        await queue.CompleteAsync(claim!, sent: true, CancellationToken.None);

        context.ChangeTracker.Clear();
        var notification = await context.Notifications.FindAsync(claim!.NotificationId);
        var work = await context.AlertDeliveryWork.FindAsync(claim.NotificationId);
        notification!.Status.Should().Be(NotificationStatus.Sent);
        notification.SentAtUtc.Should().Be(Now);
        work!.ClaimToken.Should().BeNull();
        work.ClaimExpiresAtUtc.Should().BeNull();
        work.NextAttemptAtUtc.Should().BeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClaimNextAsync_WithoutActiveToken_StopsWithoutIncrementingAttempts()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database, includeToken: false);
        await using var context = database.CreateDbContext();
        var queue = new AlertDeliveryQueue(context, new Clock(),
            Options.Create(new TripAlertOptions()));

        (await queue.ClaimNextAsync(CancellationToken.None)).Should().BeNull();

        context.ChangeTracker.Clear();
        (await context.AlertDeliveryWork.SingleAsync()).AttemptCount.Should().Be(0);
        (await context.AlertDeliveryWork.SingleAsync()).NextAttemptAtUtc.Should().BeNull();
        (await context.Notifications.SingleAsync()).Status.Should().Be(NotificationStatus.Failed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CompleteAsync_RetryableFailuresUse15Then30MinutesAndStopAfterThreeAttempts()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var clock = new Clock();
        var queue = new AlertDeliveryQueue(context, clock, Options.Create(new TripAlertOptions()));

        var first = (await queue.ClaimNextAsync(CancellationToken.None))!;
        await queue.CompleteAsync(first, sent: false, CancellationToken.None);
        context.ChangeTracker.Clear();
        (await context.AlertDeliveryWork.SingleAsync()).NextAttemptAtUtc
            .Should().Be(Now.AddMinutes(15));

        clock.UtcNow = Now.AddMinutes(15);
        var second = (await queue.ClaimNextAsync(CancellationToken.None))!;
        second.AttemptCount.Should().Be(2);
        await queue.CompleteAsync(second, sent: false, CancellationToken.None);
        context.ChangeTracker.Clear();
        (await context.AlertDeliveryWork.SingleAsync()).NextAttemptAtUtc
            .Should().Be(Now.AddMinutes(45));

        clock.UtcNow = Now.AddMinutes(45);
        var third = (await queue.ClaimNextAsync(CancellationToken.None))!;
        third.AttemptCount.Should().Be(3);
        await queue.CompleteAsync(third, sent: false, CancellationToken.None);
        context.ChangeTracker.Clear();
        (await context.AlertDeliveryWork.SingleAsync()).NextAttemptAtUtc.Should().BeNull();
        (await context.Notifications.SingleAsync()).Status.Should().Be(NotificationStatus.Failed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClaimNextAsync_AfterLeaseExpires_ReclaimsWithNewTokenAndAttempt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var firstContext = database.CreateDbContext();
        var options = Options.Create(new TripAlertOptions());
        var first = (await new AlertDeliveryQueue(firstContext, new Clock(), options)
            .ClaimNextAsync(CancellationToken.None))!;

        await using var secondContext = database.CreateDbContext();
        var secondClock = new Clock { UtcNow = Now + options.Value.DeliveryLease + TimeSpan.FromSeconds(1) };
        var second = (await new AlertDeliveryQueue(secondContext, secondClock, options)
            .ClaimNextAsync(CancellationToken.None))!;

        second.AttemptCount.Should().Be(2);
        second.ClaimToken.Should().NotBe(first.ClaimToken);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CompleteAsync_InvalidToken_DeactivatesOnlyThatToken()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var queue = new AlertDeliveryQueue(context, new Clock(),
            Options.Create(new TripAlertOptions()));
        var claim = (await queue.ClaimNextAsync(CancellationToken.None))!;

        await queue.CompleteAsync(
            claim,
            new TripAlertPushResult(false, ["protected-token"]),
            CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.PushDeviceTokens.SingleAsync()).DeactivatedAtUtc.Should().Be(Now);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CompleteAsync_AfterAcknowledgement_DoesNotOverwriteReadStatus()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var queue = new AlertDeliveryQueue(context, new Clock(),
            Options.Create(new TripAlertOptions()));
        var claim = (await queue.ClaimNextAsync(CancellationToken.None))!;
        var notification = await context.Notifications.SingleAsync();
        notification.MarkRead(Now.AddSeconds(1));
        await context.SaveChangesAsync();

        await queue.CompleteAsync(claim, sent: true, CancellationToken.None);

        context.ChangeTracker.Clear();
        (await context.Notifications.SingleAsync()).Status.Should().Be(NotificationStatus.Read);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClaimNextAsync_WhenIncidentResolved_StopsWithoutIncrementingAttemptsAndSetsTerminalFailed()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var incident = await context.Incidents.SingleAsync();
        incident.Resolve(Now.AddMinutes(1));
        await context.SaveChangesAsync();

        var queue = new AlertDeliveryQueue(context, new Clock(), Options.Create(new TripAlertOptions()));
        var claim = await queue.ClaimNextAsync(CancellationToken.None);

        claim.Should().BeNull();
        context.ChangeTracker.Clear();
        var work = await context.AlertDeliveryWork.SingleAsync();
        work.NextAttemptAtUtc.Should().BeNull();
        work.AttemptCount.Should().Be(0);
        var notification = await context.Notifications.SingleAsync();
        notification.Status.Should().Be(NotificationStatus.Failed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClaimNextAsync_WhenSessionEnded_StopsWithoutIncrementingAttemptsAndSetsTerminalFailed()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await SeedAsync(database);
        await using var context = database.CreateDbContext();
        var session = await context.TripSessions.SingleAsync();
        session.Finish(Now.AddMinutes(1));
        await context.SaveChangesAsync();

        var queue = new AlertDeliveryQueue(context, new Clock(), Options.Create(new TripAlertOptions()));
        var claim = await queue.ClaimNextAsync(CancellationToken.None);

        claim.Should().BeNull();
        context.ChangeTracker.Clear();
        var work = await context.AlertDeliveryWork.SingleAsync();
        work.NextAttemptAtUtc.Should().BeNull();
        work.AttemptCount.Should().Be(0);
        var notification = await context.Notifications.SingleAsync();
        notification.Status.Should().Be(NotificationStatus.Failed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Persistence_WhenFailureOccurs_RollsBackIncidentNotificationAndWork()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await using var context = database.CreateDbContext();
        var user = new User
        {
            Email = "tx@example.com",
            FullName = "Tx User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var category = PoiCategory.Create("Tx", null);
        context.AddRange(user, category);
        await context.SaveChangesAsync();
        var poi = PointOfInterest.Create(category, "Tx POI", 16m, 108m, user.Id, Now);
        context.Add(poi);
        await context.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(user.Id, "Tx", Itinerary.ActiveStatus, Now);
        itinerary.AddItem(ItineraryItem.CreateVisit(1, poi.Id, Now.AddMinutes(10), Now.AddMinutes(40), true, 0, "Tx"));
        context.Add(itinerary);
        await context.SaveChangesAsync();
        var session = TripSession.Start(itinerary.Id, itinerary.Id, user.Id, Guid.NewGuid(), Now, Now.AddHours(2),
            [TripSessionItem.Snapshot(itinerary.Items.Single().Id, 1, poi.Id, poi.Name, poi.Latitude, poi.Longitude, Now.AddMinutes(10), Now.AddMinutes(40), true)]);
        context.Add(session);
        await context.SaveChangesAsync();

        var persistence = new TripAlertPersistence(context);

        var act = async () =>
        {
            await context.ExecuteInTransactionAsync<bool>(async ct =>
            {
                await persistence.CreateAsync(
                    session,
                    Incident.SevereWeatherType,
                    "tx-test-source",
                    Now,
                    null,
                    null,
                    "Rollback test",
                    ct);

                // Simulate unexpected failure during the atomic operation
                throw new InvalidOperationException("Simulated forced persistence failure.");
            }, CancellationToken.None);
        };

        await act.Should().ThrowAsync<InvalidOperationException>();

        context.ChangeTracker.Clear();
        (await context.Incidents.CountAsync(i => i.SourceKey == "tx-test-source")).Should().Be(0);
        (await context.Notifications.CountAsync(n => n.UserId == user.Id)).Should().Be(0);
        (await context.AlertDeliveryWork.CountAsync()).Should().Be(0);
    }

    private static async Task<ClaimedTripAlert?> ClaimAfterGate(
        AlertDeliveryQueue queue,
        Task gate)
    {
        await gate;
        return await queue.ClaimNextAsync(CancellationToken.None);
    }

    private static async Task SeedAsync(SqlServerTestDatabase database, bool includeToken = true)
    {
        await using var db = database.CreateDbContext();
        var user = new User
        {
            Email = $"uc14-delivery-{Guid.NewGuid():N}@example.com",
            FullName = "UC14 Delivery",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var category = PoiCategory.Create($"UC14-{Guid.NewGuid():N}", null);
        db.AddRange(user, category);
        await db.SaveChangesAsync();
        var poi = PointOfInterest.Create(category, "Delivery POI", 16m, 108m, user.Id, Now);
        db.Add(poi);
        await db.SaveChangesAsync();
        var itinerary = Itinerary.CreateManual(user.Id, "Delivery", Itinerary.ActiveStatus, Now);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1, poi.Id, Now.AddMinutes(10), Now.AddMinutes(40), true, 0, "Delivery test"));
        db.Add(itinerary);
        await db.SaveChangesAsync();
        var itineraryItem = itinerary.Items.Single();
        var session = TripSession.Start(
            itinerary.Id,
            itinerary.Id,
            user.Id,
            Guid.NewGuid(),
            Now,
            Now.AddHours(2),
            [TripSessionItem.Snapshot(
                itineraryItem.Id,
                1,
                poi.Id,
                poi.Name,
                poi.Latitude,
                poi.Longitude,
                itineraryItem.PlannedArrivalUtc!.Value,
                itineraryItem.PlannedDepartureUtc!.Value,
                true)]);
        db.Add(session);
        await db.SaveChangesAsync();
        var incident = Incident.CreateTripAlert(
            session,
            Incident.ScheduleDelayType,
            $"delay:{itineraryItem.Id}",
            Now,
            itineraryItem.Id);
        db.Add(incident);
        await db.SaveChangesAsync();
        var notification = new Notification
        {
            UserId = user.Id,
            Channel = NotificationChannel.Push,
            Type = Notification.TripAlertType,
            Title = "Trip alert",
            Body = "Review your trip.",
            RelatedEntityType = Notification.IncidentRelatedEntityType,
            RelatedEntityId = incident.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = Now,
        };
        db.Add(notification);
        await db.SaveChangesAsync();
        db.Add(AlertDeliveryWork.Create(notification, Now));
        if (includeToken)
        {
            db.Add(PushDeviceToken.Register(
                user,
                Guid.NewGuid(),
                "protected-token",
                new string('A', PushDeviceToken.TokenHashLength),
                Now));
        }
        await db.SaveChangesAsync();
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }
}
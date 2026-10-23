using FluentAssertions;

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

    private static async Task<ClaimedTripAlert?> ClaimAfterGate(
        AlertDeliveryQueue queue,
        Task gate)
    {
        await gate;
        return await queue.ClaimNextAsync(CancellationToken.None);
    }

    private static async Task SeedAsync(SqlServerTestDatabase database)
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
        db.Add(PushDeviceToken.Register(
            user,
            Guid.NewGuid(),
            "protected-token",
            new string('A', PushDeviceToken.TokenHashLength),
            Now));
        await db.SaveChangesAsync();
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => Now;
    }
}
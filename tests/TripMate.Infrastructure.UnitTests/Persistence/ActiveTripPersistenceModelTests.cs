using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class ActiveTripPersistenceModelTests
{
    [Fact]
    public void Model_MapsExistingLiveTripSchemaAndUtcColumns()
    {
        using var context = CreateContext();

        var session = context.Model.FindEntityType(typeof(TripSession))!;
        session.GetTableName().Should().Be("TripSessions");
        session.GetSchema().Should().Be("trip");
        Column(session, nameof(TripSession.Id)).Should().Be("session_id");
        Column(session, nameof(TripSession.StartedAtUtc)).Should().Be("started_at");
        session.FindProperty(nameof(TripSession.StartedAtUtc))!.GetValueConverter().Should().NotBeNull();

        var incident = context.Model.FindEntityType(typeof(Incident))!;
        incident.GetTableName().Should().Be("Incidents");
        incident.GetSchema().Should().Be("trip");
        Column(incident, nameof(Incident.ResolvedAtUtc)).Should().Be("resolved_at");
        incident.FindProperty(nameof(Incident.ResolvedAtUtc))!.GetValueConverter().Should().NotBeNull();

        var itinerary = context.Model.FindEntityType(typeof(Itinerary))!;
        Column(itinerary, nameof(Itinerary.SourceTourId)).Should().Be("source_tour_id");
        itinerary.FindNavigation(nameof(Itinerary.SourceTour)).Should().NotBeNull();
        Itinerary.BookedTourSourceType.Should().Be("BookedTour");
    }

    [Fact]
    public void Model_MapsNavigationExecutionSnapshotHistoryAndConcurrencyToken()
    {
        using var context = CreateContext();

        var session = context.Model.FindEntityType(typeof(TripSession))!;
        Column(session, nameof(TripSession.RequestedItineraryId)).Should().Be("requested_itinerary_id");
        Column(session, nameof(TripSession.StartIdempotencyKey)).Should().Be("start_idempotency_key");
        Column(session, nameof(TripSession.CompletionReason)).Should().Be("completion_reason");
        Column(session, nameof(TripSession.RowVersion)).Should().Be("row_version");
        session.FindProperty(nameof(TripSession.RowVersion))!.IsConcurrencyToken.Should().BeTrue();
        session.FindProperty(nameof(TripSession.RowVersion))!.ValueGenerated
            .Should().Be(ValueGenerated.OnAddOrUpdate);
        session.FindProperty(nameof(TripSession.StartIdempotencyKey))!.IsNullable.Should().BeFalse();
        session.FindProperty(nameof(TripSession.CompletionReason))!.GetMaxLength().Should().Be(24);
        Column(session, nameof(TripSession.ExpiresAtUtc)).Should().Be("expires_at");
        session.FindProperty(nameof(TripSession.ExpiresAtUtc))!.GetValueConverter().Should().NotBeNull();
        Column(session, nameof(TripSession.ExploringItemId)).Should().Be("exploring_item_id");
        session.GetIndexes().Single(index => index.GetDatabaseName() == "UQ_TripSessions_Traveler_StartKey")
            .IsUnique.Should().BeTrue();
        var openSessionIndex = session.GetIndexes()
            .Single(index => index.GetDatabaseName() == "UQ_TripSessions_OpenTraveler");
        openSessionIndex.IsUnique.Should().BeTrue();
        openSessionIndex.GetFilter().Should().Be("[ended_at] IS NULL");

        var item = context.Model.FindEntityType(typeof(TripSessionItem))!;
        item.GetTableName().Should().Be("TripSessionItems");
        item.GetSchema().Should().Be("trip");
        item.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(TripSessionItem.SessionId), nameof(TripSessionItem.ItineraryItemId));
        item.FindProperty(nameof(TripSessionItem.PoiName))!.GetMaxLength()
            .Should().Be(PointOfInterest.NameMaxLength);
        item.FindProperty(nameof(TripSessionItem.Latitude))!.GetPrecision().Should().Be(9);
        item.FindProperty(nameof(TripSessionItem.Latitude))!.GetScale().Should().Be(6);
        foreach (var (property, column) in new[]
        {
            (nameof(TripSessionItem.PlannedArrivalUtc), "planned_arrival"),
            (nameof(TripSessionItem.PlannedDepartureUtc), "planned_departure"),
            (nameof(TripSessionItem.ReachedAtUtc), "reached_at"),
            (nameof(TripSessionItem.SkippedAtUtc), "skipped_at"),
        })
        {
            Column(item, property).Should().Be(column);
            item.FindProperty(property)!.GetValueConverter().Should().NotBeNull();
        }

        Column(item, nameof(TripSessionItem.IsMandatory)).Should().Be("is_mandatory");
        item.FindProperty(nameof(TripSessionItem.Status)).Should().BeNull();
        item.GetIndexes().Single(index => index.GetDatabaseName() == "UQ_TripSessionItems_Sequence")
            .IsUnique.Should().BeTrue();
        item.GetIndexes().Should().ContainSingle(index =>
            index.GetDatabaseName() == "IX_TripSessionItems_Next");

        var history = context.Model.FindEntityType(typeof(TripStateHistory))!;
        history.GetTableName().Should().Be("TripStateHistory");
        history.GetSchema().Should().Be("trip");
        history.FindProperty(nameof(TripStateHistory.ChangedAtUtc))!.GetValueConverter()
            .Should().NotBeNull();
        history.GetForeignKeys().Single().DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }

    [Fact]
    public void Model_MapsUc14AlertIdentityReadStateDeliveryAndDeviceRegistry()
    {
        using var context = CreateContext();

        var incident = context.Model.FindEntityType(typeof(Incident))!;
        Column(incident, nameof(Incident.AffectedItineraryItemId))
            .Should().Be("affected_itinerary_item_id");
        Column(incident, nameof(Incident.SourceKey)).Should().Be("source_key");
        Column(incident, nameof(Incident.ClientEventId)).Should().Be("client_event_id");
        var openSource = incident.GetIndexes().Single(index =>
            index.GetDatabaseName() == "UQ_Incidents_OpenSource");
        openSource.IsUnique.Should().BeTrue();
        openSource.GetFilter().Should().Be("[resolved_at] IS NULL AND [source_key] IS NOT NULL");
        var clientEvent = incident.GetIndexes().Single(index =>
            index.GetDatabaseName() == "UQ_Incidents_ClientEvent");
        clientEvent.IsUnique.Should().BeTrue();
        clientEvent.GetFilter().Should().Be("[client_event_id] IS NOT NULL");

        var notification = context.Model.FindEntityType(typeof(Notification))!;
        Column(notification, nameof(Notification.ReadAtUtc)).Should().Be("read_at");
        notification.FindProperty(nameof(Notification.ReadAtUtc))!.GetValueConverter()
            .Should().NotBeNull();
        var tripAlert = notification.GetIndexes().Single(index =>
            index.GetDatabaseName() == "UQ_Notifications_TripAlertIncident");
        tripAlert.IsUnique.Should().BeTrue();
        tripAlert.GetFilter().Should().Be(
            "[type] = 'TripAlert' AND [related_entity_type] = 'Incident' AND [related_entity_id] IS NOT NULL");

        var delivery = context.Model.FindEntityType(typeof(AlertDeliveryWork))!;
        delivery.GetTableName().Should().Be("AlertDeliveryWork");
        delivery.GetSchema().Should().Be("trip");
        delivery.FindPrimaryKey()!.Properties.Should().ContainSingle()
            .Which.Name.Should().Be(nameof(AlertDeliveryWork.NotificationId));
        Column(delivery, nameof(AlertDeliveryWork.NextAttemptAtUtc)).Should().Be("next_attempt_at");
        Column(delivery, nameof(AlertDeliveryWork.ClaimExpiresAtUtc)).Should().Be("claim_expires_at");

        var device = context.Model.FindEntityType(typeof(PushDeviceToken))!;
        device.GetTableName().Should().Be("PushDeviceTokens");
        device.GetSchema().Should().Be("dbo");
        device.GetIndexes().Single(index => index.GetDatabaseName() == "UQ_PushDeviceTokens_Installation")
            .IsUnique.Should().BeTrue();
        device.GetIndexes().Single(index => index.GetDatabaseName() == "UQ_PushDeviceTokens_TokenHash")
            .IsUnique.Should().BeTrue();
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static string? Column(IEntityType entityType, string propertyName)
    {
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        return entityType.FindProperty(propertyName)!.GetColumnName(table);
    }
}
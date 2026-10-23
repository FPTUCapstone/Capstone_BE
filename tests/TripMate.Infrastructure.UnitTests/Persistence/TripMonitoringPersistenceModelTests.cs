using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class TripMonitoringPersistenceModelTests
{
    [Fact]
    public void Model_MapsTripMonitoringSchemaAndUtcColumns()
    {
        using var context = CreateContext();

        var history = context.Model.FindEntityType(typeof(TripStateHistory))!;
        history.GetTableName().Should().Be("TripStateHistory");
        history.GetSchema().Should().Be("trip");
        Column(history, nameof(TripStateHistory.Id)).Should().Be("history_id");
        Column(history, nameof(TripStateHistory.FromState)).Should().Be("from_state");
        Column(history, nameof(TripStateHistory.ToState)).Should().Be("to_state");
        Column(history, nameof(TripStateHistory.TriggeredBy)).Should().Be("triggered_by");
        history.FindProperty(nameof(TripStateHistory.ChangedAtUtc))!.GetValueConverter().Should().NotBeNull();

        var log = context.Model.FindEntityType(typeof(TripLocationLog))!;
        log.GetTableName().Should().Be("TripLocationLogs");
        log.GetSchema().Should().Be("trip");
        Column(log, nameof(TripLocationLog.Id)).Should().Be("log_id");
        Column(log, nameof(TripLocationLog.IsOfflineCaptured)).Should().Be("is_offline_captured");
        log.FindProperty(nameof(TripLocationLog.RecordedAtUtc))!.GetValueConverter().Should().NotBeNull();
        log.FindProperty(nameof(TripLocationLog.SyncedAtUtc))!.IsNullable.Should().BeTrue();

        var weather = context.Model.FindEntityType(typeof(WeatherEvent))!;
        weather.GetTableName().Should().Be("WeatherEvents");
        weather.GetSchema().Should().Be("trip");
        Column(weather, nameof(WeatherEvent.Id)).Should().Be("weather_event_id");
        Column(weather, nameof(WeatherEvent.Severity)).Should().Be("severity");
        Column(weather, nameof(WeatherEvent.EventType)).Should().Be("event_type");
        weather.FindProperty(nameof(WeatherEvent.ValidFromUtc))!.GetValueConverter().Should().NotBeNull();
        weather.FindProperty(nameof(WeatherEvent.ValidToUtc))!.IsNullable.Should().BeTrue();

        var rerouting = context.Model.FindEntityType(typeof(ReroutingEvent))!;
        rerouting.GetTableName().Should().Be("ReroutingEvents");
        rerouting.GetSchema().Should().Be("trip");
        Column(rerouting, nameof(ReroutingEvent.Id)).Should().Be("rerouting_id");
        Column(rerouting, nameof(ReroutingEvent.Status)).Should().Be("status");
        Column(rerouting, nameof(ReroutingEvent.ProposedItinerarySnapshot)).Should().Be("proposed_itinerary_snapshot");
        rerouting.FindProperty(nameof(ReroutingEvent.ProposedAtUtc))!.GetValueConverter().Should().NotBeNull();
        rerouting.FindProperty(nameof(ReroutingEvent.DecidedAtUtc))!.IsNullable.Should().BeTrue();
        rerouting.GetForeignKeys().Should().Contain(key => key.PrincipalEntityType.ClrType == typeof(Incident));
    }

    [Fact]
    public void ItineraryItem_MapsStatusAndNullablePlannedTimestamps()
    {
        using var context = CreateContext();

        var item = context.Model.FindEntityType(typeof(ItineraryItem))!;
        Column(item, nameof(ItineraryItem.Status)).Should().Be("status");
        item.FindProperty(nameof(ItineraryItem.Status))!.IsNullable.Should().BeFalse();
        item.FindProperty(nameof(ItineraryItem.PlannedArrivalUtc))!.IsNullable.Should().BeTrue();
        item.FindProperty(nameof(ItineraryItem.PlannedDepartureUtc))!.IsNullable.Should().BeTrue();
        ItineraryItem.StatusPlanned.Should().Be("Planned");
        ItineraryItem.StatusVisited.Should().Be("Visited");
        ItineraryItem.StatusSkipped.Should().Be("Skipped");
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
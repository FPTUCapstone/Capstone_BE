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
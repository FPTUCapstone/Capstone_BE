using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class RecommendationBehaviorEventPersistenceModelTests
{
    [Fact]
    public void Model_MapsRecommendationBehaviorEventToApprovedSchema()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var behaviorEvent = model.FindEntityType(typeof(RecommendationBehaviorEvent));
        behaviorEvent.Should().NotBeNull();
        behaviorEvent!.GetSchema().Should().Be("social");
        behaviorEvent.GetTableName().Should().Be("RecommendationBehaviorEvents");

        AssertProperty(behaviorEvent, nameof(RecommendationBehaviorEvent.Id), "event_id", typeof(long), false);
        Property(behaviorEvent, nameof(RecommendationBehaviorEvent.Id)).ValueGenerated
            .Should().Be(ValueGenerated.OnAdd);
        AssertProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.TravelerUserId),
            "traveler_user_id",
            typeof(long),
            false);
        AssertProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.PointOfInterestId),
            "poi_id",
            typeof(long),
            false);
        AssertProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.ItineraryId),
            "itinerary_id",
            typeof(long?),
            true);
        AssertEnumProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.EventType),
            "event_type",
            typeof(RecommendationEventType));
        AssertProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.OriginalPosition),
            "original_position",
            typeof(int?),
            true);
        AssertProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.NewPosition),
            "new_position",
            typeof(int?),
            true);
        AssertProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.WasMandatory),
            "was_mandatory",
            typeof(bool?),
            true);
        AssertEnumProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.Source),
            "source",
            typeof(RecommendationCaptureSource));
        AssertProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.OccurredAtUtc),
            "occurred_at_utc",
            typeof(DateTimeOffset),
            false);
        Property(behaviorEvent, nameof(RecommendationBehaviorEvent.OccurredAtUtc))
            .GetTypeMapping().Converter!.ProviderClrType.Should().Be(typeof(DateTime));
        Property(behaviorEvent, nameof(RecommendationBehaviorEvent.OccurredAtUtc))
            .GetDefaultValueSql().Should().Be("SYSUTCDATETIME()");
        AssertProperty(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.ClientEventId),
            "client_event_id",
            typeof(Guid),
            false);

        behaviorEvent.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(RecommendationBehaviorEvent.Id));

        var indexes = behaviorEvent.GetIndexes().ToDictionary(index => index.GetDatabaseName()!);
        indexes["UQ_RecommendationBehaviorEvents_Traveler_Client"].IsUnique.Should().BeTrue();
        indexes["UQ_RecommendationBehaviorEvents_Traveler_Client"].Properties
            .Select(property => property.Name).Should().Equal(
                nameof(RecommendationBehaviorEvent.TravelerUserId),
                nameof(RecommendationBehaviorEvent.ClientEventId));
        indexes["IX_RecommendationBehaviorEvents_Traveler_OccurredAt"].Properties
            .Select(property => property.Name).Should().Equal(
                nameof(RecommendationBehaviorEvent.TravelerUserId),
                nameof(RecommendationBehaviorEvent.OccurredAtUtc));
        indexes["IX_RecommendationBehaviorEvents_POI"].Properties
            .Select(property => property.Name).Should().Equal(
                nameof(RecommendationBehaviorEvent.PointOfInterestId));
        indexes["IX_RecommendationBehaviorEvents_Itinerary"].Properties
            .Select(property => property.Name).Should().Equal(
                nameof(RecommendationBehaviorEvent.ItineraryId));

        behaviorEvent.GetForeignKeys().Should().HaveCount(3);
        AssertForeignKey<User>(behaviorEvent, nameof(RecommendationBehaviorEvent.TravelerUserId));
        AssertForeignKey<PointOfInterest>(
            behaviorEvent,
            nameof(RecommendationBehaviorEvent.PointOfInterestId));
        AssertForeignKey<Itinerary>(behaviorEvent, nameof(RecommendationBehaviorEvent.ItineraryId));
    }

    private static void AssertEnumProperty(
        IEntityType entityType,
        string propertyName,
        string columnName,
        Type clrType)
    {
        AssertProperty(entityType, propertyName, columnName, clrType, false);
        var property = Property(entityType, propertyName);
        property.GetProviderClrType().Should().Be(typeof(string));
        property.GetMaxLength().Should().Be(20);
        property.IsUnicode().Should().BeFalse();
    }

    private static void AssertProperty(
        IEntityType entityType,
        string propertyName,
        string columnName,
        Type clrType,
        bool isNullable)
    {
        var property = Property(entityType, propertyName);
        ColumnName(entityType, propertyName).Should().Be(columnName);
        property.ClrType.Should().Be(clrType);
        property.IsNullable.Should().Be(isNullable);
    }

    private static void AssertForeignKey<TPrincipal>(IEntityType entityType, string propertyName)
    {
        var foreignKey = entityType.GetForeignKeys()
            .Single(candidate => candidate.Properties.Single().Name == propertyName);
        foreignKey.PrincipalEntityType.ClrType.Should().Be(typeof(TPrincipal));
        foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.NoAction);
    }

    private static IProperty Property(IEntityType entityType, string propertyName) =>
        entityType.FindProperty(propertyName)
        ?? throw new InvalidOperationException($"{propertyName} is not mapped.");

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(new SqlConnection())
            .Options;

        return new ApplicationDbContext(options);
    }

    private static string? ColumnName(IEntityType entityType, string propertyName)
    {
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        return Property(entityType, propertyName).GetColumnName(table);
    }
}
using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class WeatherPersistenceModelTests
{
    private static ApplicationDbContext CreateContext()
    {
        return new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
    }

    [Fact]
    public void WeatherAlertInboxItem_IsConfiguredWithSchemaAndIndices()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(WeatherAlertInboxItem));

        entity.Should().NotBeNull();
        entity!.GetSchema().Should().Be("trip");
        entity.GetTableName().Should().Be("WeatherAlertInboxItems");

        entity.FindProperty(nameof(WeatherAlertInboxItem.Id))!.GetColumnName().Should().Be("inbox_id");
        entity.FindProperty(nameof(WeatherAlertInboxItem.Provider))!.GetColumnName().Should().Be("provider");
        entity.FindProperty(nameof(WeatherAlertInboxItem.ReceivedAtUtc))!.GetValueConverter().Should().NotBeNull();
        entity.FindProperty(nameof(WeatherAlertInboxItem.ProcessedAtUtc))!.GetValueConverter().Should().NotBeNull();
        entity.FindProperty(nameof(WeatherAlertInboxItem.LeaseExpiresAtUtc))!.GetValueConverter().Should().NotBeNull();

        entity.GetIndexes().Should().Contain(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(WeatherAlertInboxItem.Provider), nameof(WeatherAlertInboxItem.ProviderNotificationId) })
            && i.IsUnique);

        entity.GetIndexes().Should().Contain(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(WeatherAlertInboxItem.Status), nameof(WeatherAlertInboxItem.LeaseExpiresAtUtc) }));
    }

    [Fact]
    public void WeatherEvent_CarriesOfficialAuthorityAndProviderColumns()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(WeatherEvent));

        entity.Should().NotBeNull();
        entity!.FindProperty(nameof(WeatherEvent.Provider))!.GetColumnName().Should().Be("provider");
        entity.FindProperty(nameof(WeatherEvent.ProviderEventId))!.GetColumnName().Should().Be("provider_event_id");
        entity.FindProperty(nameof(WeatherEvent.Revision))!.GetColumnName().Should().Be("revision");
        entity.FindProperty(nameof(WeatherEvent.OriginalAuthority))!.GetColumnName().Should().Be("original_authority");
        entity.FindProperty(nameof(WeatherEvent.OriginalSourceReference))!.GetColumnName().Should().Be("original_source_reference");
        entity.FindProperty(nameof(WeatherEvent.OfficialVietnamRiskLevel))!.GetColumnName().Should().Be("official_vietnam_risk_level");
        entity.FindProperty(nameof(WeatherEvent.VerifiedDirective))!.GetColumnName().Should().Be("verified_directive");
        entity.FindProperty(nameof(WeatherEvent.IsWithdrawn))!.GetColumnName().Should().Be("is_withdrawn");
        entity.FindProperty(nameof(WeatherEvent.GeometryGeoJson))!.GetColumnName().Should().Be("geometry_geojson");
        entity.FindProperty(nameof(WeatherEvent.RawPayloadHash))!.GetColumnName().Should().Be("raw_payload_hash");
        entity.FindProperty(nameof(WeatherEvent.UpdatedAtUtc))!.GetValueConverter().Should().NotBeNull();

        entity.GetIndexes().Should().Contain(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(WeatherEvent.Provider), nameof(WeatherEvent.ProviderEventId) })
            && i.IsUnique);
    }

    [Fact]
    public void WeatherEventEvaluation_IsConfiguredWithSchemaAndForeignKeys()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(WeatherEventEvaluation));

        entity.Should().NotBeNull();
        entity!.GetSchema().Should().Be("trip");
        entity.GetTableName().Should().Be("WeatherEventEvaluations");

        entity.FindProperty(nameof(WeatherEventEvaluation.EvaluatedAtUtc))!.GetValueConverter().Should().NotBeNull();

        entity.GetIndexes().Should().Contain(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] {
                nameof(WeatherEventEvaluation.Provider),
                nameof(WeatherEventEvaluation.ProviderEventId),
                nameof(WeatherEventEvaluation.TourScheduleId)
            }) && i.IsUnique);

        entity.FindNavigation(nameof(WeatherEventEvaluation.TourSchedule)).Should().NotBeNull();
    }

    [Fact]
    public void WeatherSafetyReview_IsConfiguredWithSchemaAndIndices()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(WeatherSafetyReview));

        entity.Should().NotBeNull();
        entity!.GetSchema().Should().Be("trip");
        entity.GetTableName().Should().Be("WeatherSafetyReviews");

        entity.FindProperty(nameof(WeatherSafetyReview.UrgentNoticeSentAtUtc))!.GetValueConverter().Should().NotBeNull();
        entity.FindProperty(nameof(WeatherSafetyReview.AcknowledgementDeadlineUtc))!.GetValueConverter().Should().NotBeNull();
        entity.FindProperty(nameof(WeatherSafetyReview.AcknowledgedAtUtc))!.GetValueConverter().Should().NotBeNull();
        entity.FindProperty(nameof(WeatherSafetyReview.EscalatedAtUtc))!.GetValueConverter().Should().NotBeNull();
        entity.FindProperty(nameof(WeatherSafetyReview.CreatedAtUtc))!.GetValueConverter().Should().NotBeNull();

        entity.GetIndexes().Should().Contain(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] {
                nameof(WeatherSafetyReview.TourScheduleId),
                nameof(WeatherSafetyReview.Provider),
                nameof(WeatherSafetyReview.ProviderEventId)
            }) && i.IsUnique);

        entity.FindNavigation(nameof(WeatherSafetyReview.TourSchedule)).Should().NotBeNull();
    }

    [Fact]
    public void WeatherLocationMapping_IsConfiguredWithSchemaAndUniqueCoordinates()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(WeatherLocationMapping));

        entity.Should().NotBeNull();
        entity!.GetSchema().Should().Be("trip");
        entity.GetTableName().Should().Be("WeatherLocationMappings");

        entity.FindProperty(nameof(WeatherLocationMapping.CreatedAtUtc))!.GetValueConverter().Should().NotBeNull();
        entity.FindProperty(nameof(WeatherLocationMapping.UpdatedAtUtc))!.GetValueConverter().Should().NotBeNull();
        entity.FindProperty(nameof(WeatherLocationMapping.LastDepartureEndAtUtc))!.GetValueConverter().Should().NotBeNull();

        entity.GetIndexes().Should().Contain(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(WeatherLocationMapping.TripMateLocationKey) })
            && i.IsUnique);

        entity.GetIndexes().Should().Contain(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(WeatherLocationMapping.TomorrowLocationId) })
            && i.IsUnique);
    }

    [Fact]
    public void EmergencyTourCancellationOperation_RequiresOperatorActor()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(EmergencyTourCancellationOperation));

        entity.Should().NotBeNull();
        entity!.FindProperty(nameof(EmergencyTourCancellationOperation.ActorUserId))!.IsNullable.Should().BeFalse();
    }
}

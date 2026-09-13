using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class PoiPersistenceModelTests
{
    [Fact]
    public void Model_MapsPoiAggregateToSqlV7Schema()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var poi = model.FindEntityType(typeof(PointOfInterest));
        poi.Should().NotBeNull();
        poi!.GetSchema().Should().Be("catalog");
        poi.GetTableName().Should().Be("POIs");
        ColumnName(poi, nameof(PointOfInterest.Id)).Should().Be("poi_id");
        ColumnName(poi, nameof(PointOfInterest.CategoryId)).Should().Be("category_id");
        ColumnName(poi, nameof(PointOfInterest.HasShelter)).Should().Be("has_shelter");
        poi.FindProperty(nameof(PointOfInterest.Name))!.GetMaxLength()
            .Should().Be(PointOfInterest.NameMaxLength);
        poi.FindProperty(nameof(PointOfInterest.Latitude))!.GetPrecision().Should().Be(9);
        poi.FindProperty(nameof(PointOfInterest.Latitude))!.GetScale().Should().Be(6);
        poi.FindProperty(nameof(PointOfInterest.Longitude))!.GetPrecision().Should().Be(9);
        poi.FindProperty(nameof(PointOfInterest.Longitude))!.GetScale().Should().Be(6);
        poi.FindProperty(nameof(PointOfInterest.IndoorOutdoor))!
            .GetProviderClrType().Should().Be(typeof(string));
        poi.FindProperty(nameof(PointOfInterest.Status))!
            .GetProviderClrType().Should().Be(typeof(string));
        poi.FindProperty(nameof(PointOfInterest.CreatedAtUtc))!
            .GetTypeMapping().Converter!.ProviderClrType.Should().Be(typeof(DateTime));
        poi.FindProperty(nameof(PointOfInterest.IndoorOutdoor))!
            .IsUnicode().Should().BeFalse();
        poi.FindProperty(nameof(PointOfInterest.Status))!
            .IsUnicode().Should().BeFalse();
        poi.FindProperty(nameof(PointOfInterest.AverageVisitDurationMinutes))!
            .GetDefaultValue().Should()
            .Be(PointOfInterest.DefaultAverageVisitDurationMinutes);

        var category = model.FindEntityType(typeof(PoiCategory));
        category.Should().NotBeNull();
        category!.GetSchema().Should().Be("catalog");
        category.GetTableName().Should().Be("POICategories");
        category.FindProperty(nameof(PoiCategory.Id))!.GetColumnType().Should().Be("int");

        var openingHour = model.FindEntityType(typeof(PoiOpeningHour));
        openingHour.Should().NotBeNull();
        openingHour!.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(PoiOpeningHour.PointOfInterestId), nameof(PoiOpeningHour.DayOfWeek));
        openingHour.GetForeignKeys().Single().DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        var poiTag = model.FindEntityType(typeof(PoiTag));
        poiTag.Should().NotBeNull();
        poiTag!.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(PoiTag.PointOfInterestId), nameof(PoiTag.TagId));

        var auditLog = model.FindEntityType(typeof(AuditLog));
        auditLog.Should().NotBeNull();
        auditLog!.GetSchema().Should().Be("dbo");
        auditLog.GetTableName().Should().Be("AuditLogs");
        auditLog.FindProperty(nameof(AuditLog.ActionType))!.IsUnicode().Should().BeFalse();
        auditLog.GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_AuditLogs_Actor_Date")
            .IsDescending.Should().Equal(false, true);
    }

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
        return entityType.FindProperty(propertyName)!.GetColumnName(table);
    }
}
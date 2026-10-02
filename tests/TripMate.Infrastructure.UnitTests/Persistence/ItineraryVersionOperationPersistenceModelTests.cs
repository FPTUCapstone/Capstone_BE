using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class ItineraryVersionOperationPersistenceModelTests
{
    [Fact]
    public void Model_MapsItineraryVersionAndOperationToPlanningSchema()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var itinerary = model.FindEntityType(typeof(Itinerary));
        itinerary.Should().NotBeNull();
        ColumnName(itinerary!, nameof(Itinerary.Version)).Should().Be("version");

        var operation = model.FindEntityType(typeof(ItineraryVersionOperation));
        operation.Should().NotBeNull();
        operation!.GetSchema().Should().Be("planning");
        operation.GetTableName().Should().Be("ItineraryVersionOperations");
        ColumnName(operation, nameof(ItineraryVersionOperation.Id)).Should().Be("operation_id");
        ColumnName(operation, nameof(ItineraryVersionOperation.CreatedAtUtc)).Should().Be("created_at");
        operation.FindProperty(nameof(ItineraryVersionOperation.CreatedAtUtc))!
            .GetTypeMapping().Converter!.ProviderClrType.Should().Be(typeof(DateTime));
        operation.GetIndexes().Should().ContainSingle(index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(ItineraryVersionOperation.TravelerUserId), nameof(ItineraryVersionOperation.IdempotencyKey) }));
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
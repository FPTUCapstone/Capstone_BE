using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class SchedulingGenerationPersistenceModelTests
{
    [Fact]
    public void Model_MapsSchedulingRequestAndTypedItineraryItemsToPlanningSchema()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var request = model.FindEntityType(typeof(SchedulingRequest));
        request.Should().NotBeNull();
        request!.GetSchema().Should().Be("planning");
        request.GetTableName().Should().Be("SchedulingRequests");
        ColumnName(request, nameof(SchedulingRequest.Id)).Should().Be("request_id");
        ColumnName(request, nameof(SchedulingRequest.IdempotencyKey)).Should().Be("idempotency_key");
        request.FindProperty(nameof(SchedulingRequest.StartAtUtc))!
            .GetTypeMapping().Converter!.ProviderClrType.Should().Be(typeof(DateTime));
        request.GetIndexes().Should().ContainSingle(index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(SchedulingRequest.TravelerUserId), nameof(SchedulingRequest.IdempotencyKey) }));

        var item = model.FindEntityType(typeof(ItineraryItem));
        item.Should().NotBeNull();
        item!.GetSchema().Should().Be("planning");
        item.GetTableName().Should().Be("ItineraryItems");
        ColumnName(item, nameof(ItineraryItem.Kind)).Should().Be("item_kind");
        item.FindProperty(nameof(ItineraryItem.Kind))!.GetProviderClrType().Should().Be(typeof(string));
        item.FindProperty(nameof(ItineraryItem.PlannedArrivalUtc))!
            .GetTypeMapping().Converter!.ProviderClrType.Should().Be(typeof(DateTime));
        item.FindProperty(nameof(ItineraryItem.PointOfInterestId))!.IsNullable.Should().BeTrue();
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

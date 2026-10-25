using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class GroupLeavePersistenceModelTests
{
    [Fact]
    public void Model_MapsGroupLifecycleAndDurableLeaveOutcome()
    {
        using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(new SqlConnection()).Options);
        var model = context.GetService<IDesignTimeModel>().Model;

        var group = model.FindEntityType(typeof(TravelGroup))!;
        ColumnName(group, nameof(TravelGroup.Status)).Should().Be("status");
        ColumnName(group, nameof(TravelGroup.ClosedAtUtc)).Should().Be("closed_at");
        group.FindProperty(nameof(TravelGroup.ClosedAtUtc))!
            .GetTypeMapping().Converter!.ProviderClrType.Should().Be(typeof(DateTime?));

        var operation = model.FindEntityType(typeof(GroupLeaveOperation));
        operation.Should().NotBeNull();
        operation!.GetSchema().Should().Be("social");
        operation.GetTableName().Should().Be("GroupLeaveOperations");
        operation.GetIndexes().Should().ContainSingle(index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(GroupLeaveOperation.TravelerUserId), nameof(GroupLeaveOperation.IdempotencyKey) }));
    }

    private static string? ColumnName(IEntityType entityType, string propertyName)
    {
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        return entityType.FindProperty(propertyName)!.GetColumnName(table);
    }
}
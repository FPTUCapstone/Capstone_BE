using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class GroupMemberRemovalPersistenceModelTests
{
    [Fact]
    public void Model_MapsDurableRemovalOutcomeAndUniqueHostKey()
    {
        using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(new SqlConnection()).Options);
        var model = context.GetService<IDesignTimeModel>().Model;

        var operation = model.FindEntityType(typeof(GroupMemberRemovalOperation));
        operation.Should().NotBeNull();
        operation!.GetSchema().Should().Be("social");
        operation.GetTableName().Should().Be("GroupMemberRemovalOperations");
        ColumnName(operation, nameof(GroupMemberRemovalOperation.MemberName)).Should().Be("member_name");
        ColumnName(operation, nameof(GroupMemberRemovalOperation.RemovedAtUtc)).Should().Be("removed_at");
        operation.FindProperty(nameof(GroupMemberRemovalOperation.MemberName))!.GetMaxLength().Should().Be(150);
        operation.FindProperty(nameof(GroupMemberRemovalOperation.RemovedAtUtc))!
            .GetTypeMapping().Converter!.ProviderClrType.Should().Be(typeof(DateTime));
        operation.GetIndexes().Should().ContainSingle(index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(GroupMemberRemovalOperation.HostUserId), nameof(GroupMemberRemovalOperation.IdempotencyKey) }));
    }

    private static string? ColumnName(IEntityType entityType, string propertyName)
    {
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        return entityType.FindProperty(propertyName)!.GetColumnName(table);
    }
}

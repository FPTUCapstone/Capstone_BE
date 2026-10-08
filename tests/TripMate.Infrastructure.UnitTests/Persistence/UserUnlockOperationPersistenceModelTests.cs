using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class UserUnlockOperationPersistenceModelTests
{
    [Fact]
    public void Model_MapsUserLockMetadataAndUnlockOperationToCanonicalDatabaseObjects()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var user = model.FindEntityType(typeof(User));
        user.Should().NotBeNull();
        ColumnName(user!, nameof(User.StatusBeforeLock)).Should().Be("status_before_lock");
        ColumnName(user, nameof(User.LockedByUserId)).Should().Be("locked_by_user_id");
        ColumnName(user, nameof(User.LockedAtUtc)).Should().Be("locked_at_utc");
        ColumnName(user, nameof(User.LockReason)).Should().Be("lock_reason");

        var operation = model.FindEntityType(typeof(UserUnlockOperation));
        operation.Should().NotBeNull();
        operation!.GetSchema().Should().Be("admin");
        operation.GetTableName().Should().Be("UserUnlockOperations");
        ColumnName(operation, nameof(UserUnlockOperation.AdministratorUserId)).Should().Be("administrator_user_id");
        ColumnName(operation, nameof(UserUnlockOperation.IdempotencyKey)).Should().Be("idempotency_key");
        operation.GetIndexes().Should().ContainSingle(index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(UserUnlockOperation.AdministratorUserId), nameof(UserUnlockOperation.IdempotencyKey) }));
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
using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class TourMediaPersistenceModelTests
{
    [Fact]
    public void Model_MapsTourMediaToCanonicalSqlInventory()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var media = model.FindEntityType(typeof(TourMedia));

        media.Should().NotBeNull();
        media!.GetSchema().Should().Be("commerce");
        media.GetTableName().Should().Be("TourMedia");
        ColumnName(media, nameof(TourMedia.Id)).Should().Be("tour_media_id");
        ColumnName(media, nameof(TourMedia.CloudinaryPublicId)).Should().Be("cloudinary_public_id");
        ColumnName(media, nameof(TourMedia.DeliveryUrl)).Should().Be("delivery_url");
        ColumnName(media, nameof(TourMedia.AltText)).Should().Be("alt_text");
        media.FindProperty(nameof(TourMedia.CloudinaryPublicId))!.GetMaxLength().Should().Be(500);
        media.FindProperty(nameof(TourMedia.DeliveryUrl))!.GetMaxLength().Should().Be(1000);
        media.FindProperty(nameof(TourMedia.Caption))!.GetMaxLength().Should().Be(500);
        media.FindProperty(nameof(TourMedia.AltText))!.GetMaxLength().Should().Be(500);
        media.FindProperty(nameof(TourMedia.AltText))!.GetCollation()
            .Should().Be(TourMedia.AltTextCollation);
        media.FindProperty(nameof(TourMedia.LifecycleStatus))!.GetProviderClrType()
            .Should().Be(typeof(string));
        media.FindProperty(nameof(TourMedia.LifecycleStatus))!.GetMaxLength().Should().Be(16);
        media.FindProperty(nameof(TourMedia.LifecycleStatus))!.GetDefaultValueSql()
            .Should().Be("'Active'");
        media.GetDeclaredQueryFilters().Should().NotBeEmpty();

        AssertUtcDateTime2(media, nameof(TourMedia.CreatedAtUtc));
        AssertUtcDateTime2(media, nameof(TourMedia.UpdatedAtUtc));
        AssertUtcDateTime2(media, nameof(TourMedia.DeletedAtUtc));

        var tourForeignKey = media.GetForeignKeys().Single();
        tourForeignKey.PrincipalEntityType.ClrType.Should().Be(typeof(Tour));
        tourForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        AssertIndex(media, "UX_TourMedia_ActiveSortOrder", true,
            nameof(TourMedia.TourId), nameof(TourMedia.SortOrder));
        Index(media, "UX_TourMedia_ActiveSortOrder").GetFilter()
            .Should().Be("[lifecycle_status] = 'Active'");
        AssertIndex(media, "UX_TourMedia_ActivePrimary", true, nameof(TourMedia.TourId));
        Index(media, "UX_TourMedia_ActivePrimary").GetFilter()
            .Should().Be("[lifecycle_status] = 'Active' AND [is_primary] = 1");
        AssertIndex(media, "IX_TourMedia_TourLifecycleOrder", false,
            nameof(TourMedia.TourId), nameof(TourMedia.LifecycleStatus),
            nameof(TourMedia.SortOrder), nameof(TourMedia.Id));
        AssertIndex(media, "UX_TourMedia_CloudinaryPublicId", true,
            nameof(TourMedia.CloudinaryPublicId));
        media.GetCheckConstraints().Select(constraint => constraint.Name).Should().BeEquivalentTo(
            "CK_TourMedia_SortOrderPositive",
            "CK_TourMedia_Lifecycle",
            "CK_TourMedia_DeletedAt");
    }

    [Fact]
    public void Model_MapsUploadOperationAndCleanupOutboxToCanonicalSqlInventory()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var operation = model.FindEntityType(typeof(TourMediaUploadOperation))!;
        var cleanup = model.FindEntityType(typeof(TourMediaCleanupOutboxItem))!;

        operation.GetSchema().Should().Be("commerce");
        operation.GetTableName().Should().Be("TourMediaUploadOperations");
        ColumnName(operation, nameof(TourMediaUploadOperation.Id)).Should().Be("upload_operation_id");
        ColumnName(operation, nameof(TourMediaUploadOperation.PayloadFingerprint))
            .Should().Be("payload_fingerprint");
        operation.FindProperty(nameof(TourMediaUploadOperation.PayloadFingerprint))!
            .GetColumnType().Should().Be("char(64)");
        operation.FindProperty(nameof(TourMediaUploadOperation.PayloadFingerprint))!
            .GetCollation().Should().Be(TourMediaUploadOperation.FingerprintCollation);
        operation.FindProperty(nameof(TourMediaUploadOperation.Status))!.GetProviderClrType()
            .Should().Be(typeof(string));
        operation.FindProperty(nameof(TourMediaUploadOperation.Status))!.GetDefaultValueSql()
            .Should().Be("'Pending'");
        AssertUtcDateTime2(operation, nameof(TourMediaUploadOperation.ProviderUploadedAtUtc));
        AssertUtcDateTime2(operation, nameof(TourMediaUploadOperation.CompletedAtUtc));
        AssertUtcDateTime2(operation, nameof(TourMediaUploadOperation.CreatedAtUtc));
        AssertUtcDateTime2(operation, nameof(TourMediaUploadOperation.UpdatedAtUtc));
        operation.GetForeignKeys().Select(key => key.PrincipalEntityType.ClrType)
            .Should().BeEquivalentTo([typeof(User), typeof(Tour)]);
        operation.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(User))
            .DeleteBehavior.Should().Be(DeleteBehavior.NoAction);
        operation.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(Tour))
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        AssertIndex(operation, "UX_TourMediaUploadOperations_ActorTourKey", true,
            nameof(TourMediaUploadOperation.ActorUserId), nameof(TourMediaUploadOperation.TourId),
            nameof(TourMediaUploadOperation.IdempotencyKey));
        AssertIndex(operation, "UX_TourMediaUploadOperations_PublicId", true,
            nameof(TourMediaUploadOperation.CloudinaryPublicId));
        AssertIndex(operation, "IX_TourMediaUploadOperations_TourStatus", false,
            nameof(TourMediaUploadOperation.TourId), nameof(TourMediaUploadOperation.Status),
            nameof(TourMediaUploadOperation.Id));
        operation.GetCheckConstraints().Select(constraint => constraint.Name).Should().BeEquivalentTo(
            "CK_TourMediaUploadOperations_Fingerprint",
            "CK_TourMediaUploadOperations_Status",
            "CK_TourMediaUploadOperations_State");

        cleanup.GetSchema().Should().Be("commerce");
        cleanup.GetTableName().Should().Be("TourMediaCleanupOutbox");
        ColumnName(cleanup, nameof(TourMediaCleanupOutboxItem.Id)).Should().Be("cleanup_outbox_id");
        cleanup.FindProperty(nameof(TourMediaCleanupOutboxItem.Status))!.GetProviderClrType()
            .Should().Be(typeof(string));
        cleanup.FindProperty(nameof(TourMediaCleanupOutboxItem.Status))!.GetDefaultValueSql()
            .Should().Be("'Pending'");
        cleanup.FindProperty(nameof(TourMediaCleanupOutboxItem.AttemptCount))!.GetDefaultValue()
            .Should().Be(0);
        cleanup.FindProperty(nameof(TourMediaCleanupOutboxItem.MaxAttempts))!.GetDefaultValue()
            .Should().Be(TourMediaCleanupOutboxItem.DefaultMaxAttempts);
        AssertUtcDateTime2(cleanup, nameof(TourMediaCleanupOutboxItem.NotBeforeAtUtc));
        AssertUtcDateTime2(cleanup, nameof(TourMediaCleanupOutboxItem.LeaseExpiresAtUtc));
        AssertUtcDateTime2(cleanup, nameof(TourMediaCleanupOutboxItem.CreatedAtUtc));
        AssertUtcDateTime2(cleanup, nameof(TourMediaCleanupOutboxItem.UpdatedAtUtc));
        AssertUtcDateTime2(cleanup, nameof(TourMediaCleanupOutboxItem.CompletedAtUtc));
        cleanup.GetForeignKeys().Single().DeleteBehavior.Should().Be(DeleteBehavior.SetNull);
        AssertIndex(cleanup, "UX_TourMediaCleanupOutbox_PublicId", true,
            nameof(TourMediaCleanupOutboxItem.CloudinaryPublicId));
        AssertIndex(cleanup, "UX_TourMediaCleanupOutbox_Media", true,
            nameof(TourMediaCleanupOutboxItem.TourMediaId));
        Index(cleanup, "UX_TourMediaCleanupOutbox_Media").GetFilter()
            .Should().Be("[tour_media_id] IS NOT NULL");
        AssertIndex(cleanup, "IX_TourMediaCleanupOutbox_Due", false,
            nameof(TourMediaCleanupOutboxItem.Status), nameof(TourMediaCleanupOutboxItem.NotBeforeAtUtc),
            nameof(TourMediaCleanupOutboxItem.LeaseExpiresAtUtc), nameof(TourMediaCleanupOutboxItem.Id));
        cleanup.GetCheckConstraints().Select(constraint => constraint.Name).Should().BeEquivalentTo(
            "CK_TourMediaCleanupOutbox_Status",
            "CK_TourMediaCleanupOutbox_Attempts",
            "CK_TourMediaCleanupOutbox_State");
    }

    [Fact]
    public void StatusEnums_MatchSqlCheckConstraintValues()
    {
        Enum.GetNames<TourMediaLifecycleStatus>().Should().Equal("Active", "Deleted");
        Enum.GetNames<TourMediaUploadOperationStatus>().Should().Equal("Pending", "Uploaded", "Completed");
        Enum.GetNames<TourMediaCleanupStatus>().Should().Equal("Pending", "InProgress", "Completed", "Exhausted");
    }

    [Fact]
    public void DomainMutationProperties_DoNotExposePublicSetters()
    {
        var propertyNames = new[]
        {
            nameof(TourMedia.LifecycleStatus),
            nameof(TourMedia.DeletedAtUtc),
            nameof(TourMedia.SortOrder),
            nameof(TourMedia.IsPrimary),
            nameof(TourMediaUploadOperation.Status),
            nameof(TourMediaUploadOperation.TourMediaId),
            nameof(TourMediaCleanupOutboxItem.Status),
            nameof(TourMediaCleanupOutboxItem.AttemptCount),
        };

        propertyNames.Take(4).Select(name => typeof(TourMedia).GetProperty(name)!.SetMethod!.IsPublic)
            .Should().OnlyContain(isPublic => !isPublic);
        propertyNames.Skip(4).Take(2)
            .Select(name => typeof(TourMediaUploadOperation).GetProperty(name)!.SetMethod!.IsPublic)
            .Should().OnlyContain(isPublic => !isPublic);
        propertyNames.Skip(6)
            .Select(name => typeof(TourMediaCleanupOutboxItem).GetProperty(name)!.SetMethod!.IsPublic)
            .Should().OnlyContain(isPublic => !isPublic);
    }

    private static void AssertIndex(
        IEntityType entityType,
        string databaseName,
        bool unique,
        params string[] properties)
    {
        var index = entityType.GetIndexes()
            .Single(candidate => candidate.GetDatabaseName() == databaseName);
        index.IsUnique.Should().Be(unique);
        index.Properties.Select(property => property.Name).Should().Equal(properties);
    }

    private static IIndex Index(IEntityType entityType, string databaseName) =>
        entityType.GetIndexes().Single(candidate => candidate.GetDatabaseName() == databaseName);

    private static void AssertUtcDateTime2(IEntityType entityType, string propertyName)
    {
        var property = entityType.FindProperty(propertyName)!;
        property.GetColumnType().Should().Be("datetime2");
        property.GetTypeMapping().Converter!.ProviderClrType.Should().Be(
            property.IsNullable ? typeof(DateTime?) : typeof(DateTime));
    }

    private static string? ColumnName(IEntityType entityType, string propertyName)
    {
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        return entityType.FindProperty(propertyName)!.GetColumnName(table);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(new SqlConnection())
            .Options;

        return new ApplicationDbContext(options);
    }
}
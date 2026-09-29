using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class PoiExplorationPersistenceModelTests
{
    [Fact]
    public void Model_MapsPoiPhotoAndReviewToSqlV7Schema()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var photo = model.FindEntityType(typeof(PoiPhoto));
        photo.Should().NotBeNull();
        photo!.GetSchema().Should().Be("catalog");
        photo.GetTableName().Should().Be("POIPhotos");
        ColumnName(photo, nameof(PoiPhoto.Id)).Should().Be("photo_id");
        ColumnName(photo, nameof(PoiPhoto.PointOfInterestId)).Should().Be("poi_id");
        ColumnName(photo, nameof(PoiPhoto.Url)).Should().Be("url");
        ColumnName(photo, nameof(PoiPhoto.Caption)).Should().Be("caption");
        ColumnName(photo, nameof(PoiPhoto.SortOrder)).Should().Be("sort_order");
        photo.FindProperty(nameof(PoiPhoto.Url))!.GetMaxLength().Should().Be(500);
        photo.FindProperty(nameof(PoiPhoto.Caption))!.GetMaxLength().Should().Be(200);
        photo.FindProperty(nameof(PoiPhoto.SortOrder))!.GetDefaultValue().Should().Be(0);

        var review = model.FindEntityType(typeof(Review));
        review.Should().NotBeNull();
        review!.GetSchema().Should().Be("social");
        review.GetTableName().Should().Be("Reviews");
        ColumnName(review, nameof(Review.Id)).Should().Be("review_id");
        ColumnName(review, nameof(Review.TravelerUserId)).Should().Be("traveler_user_id");
        ColumnName(review, nameof(Review.TargetType)).Should().Be("target_type");
        ColumnName(review, nameof(Review.TargetId)).Should().Be("target_id");
        ColumnName(review, nameof(Review.Rating)).Should().Be("rating");
        ColumnName(review, nameof(Review.Comment)).Should().Be("comment");
        ColumnName(review, nameof(Review.CreatedAtUtc)).Should().Be("created_at");

        review.FindProperty(nameof(Review.TargetType))!.IsUnicode().Should().BeFalse();
        review.FindProperty(nameof(Review.TargetType))!.GetMaxLength().Should().Be(14);
        review.GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_Reviews_Target")
            .Properties.Select(property => property.Name)
            .Should().Equal(nameof(Review.TargetType), nameof(Review.TargetId));
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
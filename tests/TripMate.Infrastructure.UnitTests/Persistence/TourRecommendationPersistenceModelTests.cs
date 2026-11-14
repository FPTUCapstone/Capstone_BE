using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class TourRecommendationPersistenceModelTests
{
    [Fact]
    public void Model_MapsSearchRequestToExistingCommerceTable()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(TourSearchRequest));

        entity.Should().NotBeNull();
        entity!.GetSchema().Should().Be("commerce");
        entity.GetTableName().Should().Be("TourSearchRequests");
        ColumnName(entity, nameof(TourSearchRequest.Id)).Should().Be("search_request_id");
        entity.FindProperty(nameof(TourSearchRequest.Id))!.ValueGenerated.Should().Be(ValueGenerated.OnAdd);
        ColumnName(entity, nameof(TourSearchRequest.TravelerUserId)).Should().Be("traveler_user_id");
        ColumnName(entity, nameof(TourSearchRequest.DestinationText)).Should().Be("destination_text");
        ColumnName(entity, nameof(TourSearchRequest.StartDate)).Should().Be("start_date");
        ColumnName(entity, nameof(TourSearchRequest.EndDate)).Should().Be("end_date");
        ColumnName(entity, nameof(TourSearchRequest.MinBudget)).Should().Be("min_budget");
        ColumnName(entity, nameof(TourSearchRequest.MaxBudget)).Should().Be("max_budget");
        ColumnName(entity, nameof(TourSearchRequest.InterestTagsJson)).Should().Be("interest_tags_json");
        ColumnName(entity, nameof(TourSearchRequest.RequestedAtUtc)).Should().Be("requested_at");

        entity.FindProperty(nameof(TourSearchRequest.DestinationText))!.GetMaxLength().Should().Be(300);
        entity.FindProperty(nameof(TourSearchRequest.InterestTagsJson))!.GetMaxLength().Should().Be(1000);
        AssertDecimal(entity, nameof(TourSearchRequest.MinBudget), 12, 2);
        AssertDecimal(entity, nameof(TourSearchRequest.MaxBudget), 12, 2);
        entity.FindProperty(nameof(TourSearchRequest.StartDate))!.GetColumnType().Should().Be("date");
        entity.FindProperty(nameof(TourSearchRequest.RequestedAtUtc))!.GetColumnType().Should().Be("datetime2");

        var travelerForeignKey = entity.GetForeignKeys().Single();
        travelerForeignKey.PrincipalEntityType.ClrType.Should().Be(typeof(User));
        travelerForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.NoAction);
    }

    [Fact]
    public void Model_MapsRecommendationLogWithExpectedRelationshipsAndIndex()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(TourRecommendationLog));

        entity.Should().NotBeNull();
        entity!.GetSchema().Should().Be("commerce");
        entity.GetTableName().Should().Be("TourRecommendationLogs");
        ColumnName(entity, nameof(TourRecommendationLog.Id)).Should().Be("recommendation_id");
        ColumnName(entity, nameof(TourRecommendationLog.SearchRequestId)).Should().Be("search_request_id");
        ColumnName(entity, nameof(TourRecommendationLog.TourId)).Should().Be("tour_id");
        ColumnName(entity, nameof(TourRecommendationLog.SimilarityScore)).Should().Be("similarity_score");
        ColumnName(entity, nameof(TourRecommendationLog.IsRecommended)).Should().Be("is_recommended");
        ColumnName(entity, nameof(TourRecommendationLog.RecommendedAtUtc)).Should().Be("recommended_at");
        AssertDecimal(entity, nameof(TourRecommendationLog.SimilarityScore), 5, 2);
        entity.FindProperty(nameof(TourRecommendationLog.RecommendedAtUtc))!.GetColumnType().Should().Be("datetime2");

        var snapshotForeignKey = entity.GetForeignKeys()
            .Single(key => key.Properties.Single().Name == nameof(TourRecommendationLog.SearchRequestId));
        snapshotForeignKey.PrincipalEntityType.ClrType.Should().Be(typeof(TourSearchRequest));
        snapshotForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);

        var tourForeignKey = entity.GetForeignKeys()
            .Single(key => key.Properties.Single().Name == nameof(TourRecommendationLog.TourId));
        tourForeignKey.PrincipalEntityType.ClrType.Should().Be(typeof(Tour));
        tourForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.NoAction);

        entity.GetIndexes().Single(index => index.GetDatabaseName() == "IX_TourRecommendationLogs_Search")
            .Properties.Select(property => property.Name)
            .Should().Equal(nameof(TourRecommendationLog.SearchRequestId));
    }

    private static void AssertDecimal(IEntityType entity, string propertyName, int precision, int scale)
    {
        entity.FindProperty(propertyName)!.GetPrecision().Should().Be(precision);
        entity.FindProperty(propertyName)!.GetScale().Should().Be(scale);
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
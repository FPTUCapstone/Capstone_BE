using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class TourCategoryPersistenceModelTests
{
    [Fact]
    public void Model_MapsTransitionalTourCategoryContract()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var category = model.FindEntityType(typeof(TourCategory));
        var tour = model.FindEntityType(typeof(Tour));

        category.Should().NotBeNull();
        category!.GetSchema().Should().Be("catalog");
        category.GetTableName().Should().Be("TourCategories");
        ColumnName(category, nameof(TourCategory.Id)).Should().Be("category_id");
        ColumnName(category, nameof(TourCategory.Code)).Should().Be("code");
        ColumnName(category, nameof(TourCategory.Name)).Should().Be("name");
        ColumnName(category, nameof(TourCategory.IsActive)).Should().Be("is_active");
        category.FindProperty(nameof(TourCategory.Code))!.GetMaxLength()
            .Should().Be(TourCategory.CodeMaxLength);
        category.FindProperty(nameof(TourCategory.Code))!.GetCollation()
            .Should().Be(TourCategory.EnglishCollation);
        category.FindProperty(nameof(TourCategory.Name))!.GetMaxLength()
            .Should().Be(TourCategory.NameMaxLength);
        category.FindProperty(nameof(TourCategory.Name))!.GetCollation()
            .Should().Be(TourCategory.EnglishCollation);
        category.FindProperty(nameof(TourCategory.IsActive))!.GetDefaultValue()
            .Should().Be(true);

        AssertIndex(category, "UX_TourCategories_Code", true, nameof(TourCategory.Code));
        AssertIndex(
            category,
            "IX_TourCategories_ActiveName",
            false,
            nameof(TourCategory.IsActive),
            nameof(TourCategory.Name),
            nameof(TourCategory.Id));

        tour.Should().NotBeNull();
        ColumnName(tour!, nameof(Tour.CategoryId)).Should().Be("category_id");
        tour.FindProperty(nameof(Tour.CategoryId))!.IsNullable.Should().BeTrue();
        AssertIndex(tour, "IX_Tours_Category", false, nameof(Tour.CategoryId));
        var categoryForeignKey = tour.GetForeignKeys()
            .Single(key => key.PrincipalEntityType.ClrType == typeof(TourCategory));
        categoryForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.NoAction);
        categoryForeignKey.Properties.Select(property => property.Name)
            .Should().Equal(nameof(Tour.CategoryId));
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
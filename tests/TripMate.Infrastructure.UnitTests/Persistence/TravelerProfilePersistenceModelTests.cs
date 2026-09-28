using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class TravelerProfilePersistenceModelTests
{
    [Fact]
    public void Model_MapsTravelerProfileToSqlV7Schema()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var profile = model.FindEntityType(typeof(TravelerProfile));
        profile.Should().NotBeNull();
        profile!.GetSchema().Should().Be("dbo");
        profile.GetTableName().Should().Be("TravelerProfiles");

        var userId = Property(profile, nameof(TravelerProfile.UserId));
        AssertProperty(userId, profile, "user_id", typeof(long), isNullable: false);

        var interestTags = Property(profile, nameof(TravelerProfile.InterestTagsJson));
        AssertProperty(interestTags, profile, "interest_tags_json", typeof(string), isNullable: true);
        interestTags.GetMaxLength().Should().Be(1000);

        var preferredTransportMode = Property(profile, nameof(TravelerProfile.PreferredTransportMode));
        AssertNullableEnumProperty(
            preferredTransportMode,
            profile,
            "preferred_transport_mode",
            typeof(TransportMode?));

        var travelPace = Property(profile, nameof(TravelerProfile.TravelPace));
        AssertNullableEnumProperty(travelPace, profile, "travel_pace", typeof(TravelerPace?));

        var riskTolerance = Property(profile, nameof(TravelerProfile.RiskTolerance));
        AssertNullableEnumProperty(
            riskTolerance,
            profile,
            "risk_tolerance",
            typeof(RiskToleranceLevel?));

        var foodPreferences = Property(profile, nameof(TravelerProfile.FoodPreferencesJson));
        AssertProperty(
            foodPreferences,
            profile,
            "food_preferences_json",
            typeof(string),
            isNullable: true);
        foodPreferences.GetMaxLength().Should().Be(1000);

        var defaultBudget = Property(profile, nameof(TravelerProfile.DefaultBudget));
        AssertProperty(defaultBudget, profile, "default_budget", typeof(decimal?), isNullable: true);
        defaultBudget.GetPrecision().Should().Be(12);
        defaultBudget.GetScale().Should().Be(2);

        var updatedAt = Property(profile, nameof(TravelerProfile.UpdatedAtUtc));
        AssertProperty(updatedAt, profile, "updated_at", typeof(DateTimeOffset), isNullable: false);
        updatedAt.GetTypeMapping().Converter!.ProviderClrType.Should().Be(typeof(DateTime));

        profile.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(TravelerProfile.UserId));

        var userForeignKey = profile.GetForeignKeys().Should().ContainSingle().Subject;
        userForeignKey.Properties.Select(property => property.Name)
            .Should().Equal(nameof(TravelerProfile.UserId));
        userForeignKey.PrincipalEntityType.ClrType.Should().Be(typeof(User));
        userForeignKey.IsUnique.Should().BeTrue();
        userForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }

    private static void AssertNullableEnumProperty(
        IProperty property,
        IEntityType entityType,
        string columnName,
        Type clrType)
    {
        AssertProperty(property, entityType, columnName, clrType, isNullable: true);
        property.GetProviderClrType().Should().Be(typeof(string));
        property.GetMaxLength().Should().Be(20);
        property.IsUnicode().Should().BeFalse();
    }

    private static void AssertProperty(
        IProperty property,
        IEntityType entityType,
        string columnName,
        Type clrType,
        bool isNullable)
    {
        ColumnName(entityType, property.Name).Should().Be(columnName);
        property.ClrType.Should().Be(clrType);
        property.IsNullable.Should().Be(isNullable);
    }

    private static IProperty Property(IEntityType entityType, string propertyName)
    {
        var property = entityType.FindProperty(propertyName);
        property.Should().NotBeNull($"{propertyName} must be mapped to dbo.TravelerProfiles");
        return property!;
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

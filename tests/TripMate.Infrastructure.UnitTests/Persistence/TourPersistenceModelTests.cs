using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class TourPersistenceModelTests
{
    [Fact]
    public void Model_MapsTourToCommerceSchema()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var tour = model.FindEntityType(typeof(Tour));
        tour.Should().NotBeNull();
        tour!.GetSchema().Should().Be("commerce");
        tour.GetTableName().Should().Be("Tours");

        ColumnName(tour, nameof(Tour.Id)).Should().Be("tour_id");
        tour.FindProperty(nameof(Tour.Id))!.ValueGenerated.Should().Be(ValueGenerated.OnAdd);
        ColumnName(tour, nameof(Tour.OperatorUserId)).Should().Be("operator_user_id");
        ColumnName(tour, nameof(Tour.BasePrice)).Should().Be("base_price");
        ColumnName(tour, nameof(Tour.PublishedAtUtc)).Should().Be("published_at");

        tour.FindProperty(nameof(Tour.Title))!.GetMaxLength().Should().Be(Tour.TitleMaxLength);
        tour.FindProperty("Destination").Should().BeNull();
        var destination = model.FindEntityType(typeof(Destination))!;
        destination.GetSchema().Should().Be("catalog");
        destination.GetTableName().Should().Be("Destinations");
        ColumnName(destination, nameof(Destination.Name)).Should().Be("name");
        destination.FindProperty(nameof(Destination.Name))!.GetMaxLength()
            .Should().Be(Destination.NameMaxLength);
        destination.FindProperty(nameof(Destination.Name))!.GetCollation()
            .Should().Be(Tour.DestinationCollation);
        var link = model.FindEntityType(typeof(TourDestination))!;
        link.GetSchema().Should().Be("commerce");
        link.GetTableName().Should().Be("TourDestinations");
        link.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(TourDestination.TourId), nameof(TourDestination.DestinationId));
        link.GetForeignKeys().Should().HaveCount(2);
        tour.FindProperty(nameof(Tour.Description))!.GetMaxLength().Should().BeNull();
        tour.FindProperty(nameof(Tour.BasePrice))!.GetPrecision().Should().Be(12);
        tour.FindProperty(nameof(Tour.BasePrice))!.GetScale().Should().Be(2);
        tour.FindProperty(nameof(Tour.DurationDays))!.GetDefaultValue().Should().Be(1);
        tour.FindProperty(nameof(Tour.Status))!.GetProviderClrType().Should().Be(typeof(string));
        tour.FindProperty(nameof(Tour.Status))!.GetMaxLength().Should().Be(12);
        tour.FindProperty(nameof(Tour.Status))!.IsUnicode().Should().BeFalse();
        tour.FindProperty(nameof(Tour.Status))!.GetDefaultValueSql().Should().Be("'Draft'");

        AssertUtcDateTime2(tour, nameof(Tour.ReviewedAtUtc));
        AssertUtcDateTime2(tour, nameof(Tour.PublishedAtUtc));
        AssertUtcDateTime2(tour, nameof(Tour.CreatedAtUtc));
        AssertUtcDateTime2(tour, nameof(Tour.UpdatedAtUtc));

        var operatorForeignKey = tour.GetForeignKeys()
            .Single(foreignKey => foreignKey.Properties.Single().Name == nameof(Tour.OperatorUserId));
        operatorForeignKey.PrincipalEntityType.ClrType.Should().Be(typeof(OperatorProfile));
        operatorForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.NoAction);

        var reviewerForeignKey = tour.GetForeignKeys()
            .Single(foreignKey => foreignKey.Properties.Single().Name == nameof(Tour.ReviewedBy));
        reviewerForeignKey.PrincipalEntityType.ClrType.Should().Be(typeof(User));
        reviewerForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.NoAction);

        tour.GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_Tours_Operator")
            .Properties.Select(property => property.Name)
            .Should().Equal(nameof(Tour.OperatorUserId));
        tour.GetIndexes()
            .Single(index => index.GetDatabaseName() == "IX_Tours_Status")
            .Properties.Select(property => property.Name)
            .Should().Equal(nameof(Tour.Status));
    }

    [Fact]
    public void Model_MapsTourScheduleToCommerceSchema()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var schedule = model.FindEntityType(typeof(TourSchedule));
        schedule.Should().NotBeNull();
        schedule!.GetSchema().Should().Be("commerce");
        schedule.GetTableName().Should().Be("TourSchedules");

        ColumnName(schedule, nameof(TourSchedule.Id)).Should().Be("schedule_id");
        schedule.FindProperty(nameof(TourSchedule.Id))!.ValueGenerated.Should().Be(ValueGenerated.OnAdd);
        ColumnName(schedule, nameof(TourSchedule.TourId)).Should().Be("tour_id");
        ColumnName(schedule, nameof(TourSchedule.StartAtUtc)).Should().Be("start_datetime");
        ColumnName(schedule, nameof(TourSchedule.EndAtUtc)).Should().Be("end_datetime");
        schedule.FindProperty(nameof(TourSchedule.MeetingPoint))!.GetMaxLength()
            .Should().Be(TourSchedule.MeetingPointMaxLength);
        schedule.FindProperty(nameof(TourSchedule.ReservedCapacity))!.GetDefaultValue().Should().Be(0);
        schedule.FindProperty(nameof(TourSchedule.Status))!.GetProviderClrType()
            .Should().Be(typeof(string));
        schedule.FindProperty(nameof(TourSchedule.Status))!.GetMaxLength().Should().Be(12);
        schedule.FindProperty(nameof(TourSchedule.Status))!.IsUnicode().Should().BeFalse();
        schedule.FindProperty(nameof(TourSchedule.Status))!.GetDefaultValueSql()
            .Should().Be("'Scheduled'");

        AssertUtcDateTime2(schedule, nameof(TourSchedule.StartAtUtc));
        AssertUtcDateTime2(schedule, nameof(TourSchedule.EndAtUtc));
        AssertUtcDateTime2(schedule, nameof(TourSchedule.CreatedAtUtc));

        var tourForeignKey = schedule.GetForeignKeys().Single();
        tourForeignKey.Properties.Single().Name.Should().Be(nameof(TourSchedule.TourId));
        tourForeignKey.PrincipalEntityType.ClrType.Should().Be(typeof(Tour));
        tourForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.NoAction);

        var index = schedule.GetIndexes()
            .Single(candidate => candidate.GetDatabaseName() == "IX_TourSchedules_Tour");
        index.Properties.Select(property => property.Name)
            .Should().Equal(nameof(TourSchedule.TourId), nameof(TourSchedule.StartAtUtc));
    }

    [Fact]
    public void UtcDateTime2Converters_NormalizeOffsetsAndRoundTripAsUtc()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var tour = model.FindEntityType(typeof(Tour))!;
        var schedule = model.FindEntityType(typeof(TourSchedule))!;
        var source = new DateTimeOffset(2026, 9, 20, 0, 30, 0, TimeSpan.FromHours(7));

        AssertUtcRoundTrip(tour.FindProperty(nameof(Tour.CreatedAtUtc))!.GetTypeMapping().Converter!, source);
        AssertUtcRoundTrip(schedule.FindProperty(nameof(TourSchedule.StartAtUtc))!.GetTypeMapping().Converter!, source);
    }

    [Fact]
    public void StatusEnums_MatchSqlCheckConstraintValues()
    {
        Enum.GetNames<TourStatus>().Should()
            .Equal("Draft", "Pending", "Approved", "Rejected", "Inactive");
        Enum.GetNames<TourScheduleStatus>().Should()
            .Equal("Scheduled", "Cancelled", "Completed");
    }

    private static void AssertUtcDateTime2(IEntityType entityType, string propertyName)
    {
        var property = entityType.FindProperty(propertyName)!;
        property.GetColumnType().Should().Be("datetime2");
        property.GetTypeMapping().Converter!.ProviderClrType.Should().Be(
            property.IsNullable ? typeof(DateTime?) : typeof(DateTime));
    }

    private static void AssertUtcRoundTrip(
        ValueConverter converter,
        DateTimeOffset source)
    {
        var stored = converter.ConvertToProvider(source).Should().BeOfType<DateTime>().Subject;
        stored.Kind.Should().Be(DateTimeKind.Utc);
        stored.Should().Be(source.UtcDateTime);

        var materialized = converter.ConvertFromProvider(stored)
            .Should().BeOfType<DateTimeOffset>().Subject;
        materialized.Offset.Should().Be(TimeSpan.Zero);
        materialized.Should().Be(source.ToUniversalTime());
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
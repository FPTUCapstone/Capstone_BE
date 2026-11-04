using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;
using TripMate.Infrastructure.Persistence.Configurations;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public class ItinerarySourceBookingPersistenceModelTests
{
    [Fact]
    public void Model_MapsSourceBookingFilteredUniqueIndexExactlyLikeTheMigration()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var itinerary = model.FindEntityType(typeof(Itinerary));
        itinerary.Should().NotBeNull();

        var index = itinerary!.GetIndexes().Single(candidate =>
            candidate.Properties.Any(property => property.Name == nameof(Itinerary.SourceBookingId)));
        index.GetDatabaseName().Should().Be(ItineraryConfiguration.SourceBookingIndexName);
        index.IsUnique.Should().BeTrue();
        index.GetFilter().Should().Be("[source_booking_id] IS NOT NULL");
    }

    [Fact]
    public void Model_MapsSourceBookingIdAsNullableBookingReferenceWithRestrictedDelete()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        var itinerary = model.FindEntityType(typeof(Itinerary))!;
        var property = itinerary.FindProperty(nameof(Itinerary.SourceBookingId))!;
        property.IsNullable.Should().BeTrue();
        property.GetColumnName(StoreObjectIdentifier.Table(itinerary.GetTableName()!, itinerary.GetSchema()))
            .Should().Be("source_booking_id");

        var foreignKey = itinerary.GetForeignKeys().Single(candidate =>
            candidate.Properties.Any(p => p.Name == nameof(Itinerary.SourceBookingId)));
        foreignKey.PrincipalEntityType.ClrType.Should().Be(typeof(Booking));
        foreignKey.PrincipalKey.Properties.Single().Name.Should().Be(nameof(Booking.Id));
        foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
    }

    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(new SqlConnection())
            .Options;

        return new ApplicationDbContext(options);
    }
}

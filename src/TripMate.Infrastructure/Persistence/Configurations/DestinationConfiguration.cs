using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class DestinationConfiguration : IEntityTypeConfiguration<Destination>
{
    public void Configure(EntityTypeBuilder<Destination> builder)
    {
        builder.ToTable("Destinations", "catalog");
        builder.HasKey(destination => destination.Id);
        builder.Property(destination => destination.Id)
            .HasColumnName("destination_id").ValueGeneratedOnAdd();
        builder.Property(destination => destination.Name)
            .HasColumnName("name")
            .HasMaxLength(Destination.NameMaxLength)
            .UseCollation(Tour.DestinationCollation)
            .IsRequired();
        builder.HasIndex(destination => destination.Name)
            .IsUnique().HasDatabaseName("UX_Destinations_Name");
    }
}
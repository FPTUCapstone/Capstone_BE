using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourDestinationConfiguration : IEntityTypeConfiguration<TourDestination>
{
    public void Configure(EntityTypeBuilder<TourDestination> builder)
    {
        builder.ToTable("TourDestinations", "commerce", table =>
            table.HasCheckConstraint("CK_TourDestinations_SequencePositive", "sequence_no > 0"));
        builder.HasKey(link => new { link.TourId, link.DestinationId });
        builder.Property(link => link.TourId).HasColumnName("tour_id");
        builder.Property(link => link.DestinationId).HasColumnName("destination_id");
        builder.Property(link => link.SequenceNo).HasColumnName("sequence_no");
        builder.HasOne(link => link.Destination)
            .WithMany()
            .HasForeignKey(link => link.DestinationId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(link => new { link.TourId, link.SequenceNo })
            .IsUnique().HasDatabaseName("UX_TourDestinations_TourSequence");
        builder.HasIndex(link => link.DestinationId)
            .HasDatabaseName("IX_TourDestinations_Destination");
    }
}
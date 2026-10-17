using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourItineraryItemConfiguration : IEntityTypeConfiguration<TourItineraryItem>
{
    public void Configure(EntityTypeBuilder<TourItineraryItem> builder)
    {
        builder.ToTable("TourItineraryItems", "commerce");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("tour_item_id").ValueGeneratedOnAdd();
        builder.Property(item => item.TourId).HasColumnName("tour_id").IsRequired();
        builder.Property(item => item.DayNo).HasColumnName("day_no").IsRequired();
        builder.Property(item => item.SequenceNo).HasColumnName("sequence_no").IsRequired();
        builder.Property(item => item.PoiId).HasColumnName("poi_id").IsRequired();
        builder.Property(item => item.PlannedTime).HasColumnName("planned_time");
        builder.Property(item => item.StayDurationMinutes).HasColumnName("stay_duration_minutes").IsRequired();
        builder.Property(item => item.Notes).HasColumnName("notes").HasMaxLength(TourItineraryItem.NotesMaxLength);
        builder.HasOne(item => item.Tour).WithMany(tour => tour.ItineraryItems).HasForeignKey(item => item.TourId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Poi).WithMany().HasForeignKey(item => item.PoiId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(item => new { item.TourId, item.DayNo, item.SequenceNo }).IsUnique().HasDatabaseName("UQ_TourItineraryItems");
    }
}
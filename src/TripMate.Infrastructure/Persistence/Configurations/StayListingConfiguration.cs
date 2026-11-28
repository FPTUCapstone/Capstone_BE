using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class StayListingConfiguration : IEntityTypeConfiguration<StayListing>
{
    public void Configure(EntityTypeBuilder<StayListing> builder)
    {
        builder.ToTable("StayListings", "commercial");
        builder.HasKey(item => item.PoiId);
        builder.Property(item => item.PoiId).HasColumnName("poi_id").ValueGeneratedNever();
        builder.Property(item => item.Description).HasColumnName("description").HasMaxLength(2000);
        builder.Property(item => item.Published).HasColumnName("published");
        builder.Property(item => item.AcceptingRequests).HasColumnName("accepting_requests");
        builder.HasOne<PointOfInterest>().WithMany().HasForeignKey(item => item.PoiId).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class StayInquiryConfiguration : IEntityTypeConfiguration<StayInquiry>
{
    public void Configure(EntityTypeBuilder<StayInquiry> builder)
    {
        builder.ToTable("StayInquiries", "commercial");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("inquiry_id");
        builder.Property(item => item.PoiId).HasColumnName("poi_id");
        builder.Property(item => item.TravelerId).HasColumnName("traveler_id");
        builder.Property(item => item.ItineraryId).HasColumnName("itinerary_id");
        builder.Property(item => item.Arrival).HasColumnName("arrival").HasColumnType("date");
        builder.Property(item => item.Departure).HasColumnName("departure").HasColumnType("date");
        builder.Property(item => item.Guests).HasColumnName("guests");
        builder.Property(item => item.ContactName).HasColumnName("contact_name").HasMaxLength(150);
        builder.Property(item => item.Contact).HasColumnName("contact").HasMaxLength(254);
        builder.Property(item => item.Note).HasColumnName("note").HasMaxLength(1000);
        builder.Property(item => item.SubmitKey).HasColumnName("submit_key");
        builder.Property(item => item.SubmitHash).HasColumnName("submit_hash").HasMaxLength(64).IsUnicode(false);
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.Property(item => item.Status).HasColumnName("status").HasMaxLength(20).IsUnicode(false);
        builder.Property(item => item.Reply).HasColumnName("reply").HasMaxLength(1000);
        builder.HasIndex(item => new { item.TravelerId, item.SubmitKey }).IsUnique().HasDatabaseName("UX_StayInquiries_SubmitKey");
        builder.HasIndex(item => new { item.PoiId, item.Id }).HasDatabaseName("IX_StayInquiries_Poi");
        builder.HasOne<PointOfInterest>().WithMany().HasForeignKey(item => item.PoiId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.TravelerId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Itinerary>().WithMany().HasForeignKey(item => item.ItineraryId).OnDelete(DeleteBehavior.NoAction);
    }
}
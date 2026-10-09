using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TripReviewMediaOperationConfiguration : IEntityTypeConfiguration<TripReviewMediaOperation>
{
    public void Configure(EntityTypeBuilder<TripReviewMediaOperation> b)
    {
        b.ToTable("TripReviewMediaOperations", "social"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("operation_id").ValueGeneratedNever();
        b.Property(x => x.BatchId).HasColumnName("batch_id");
        b.Property(x => x.BookingId).HasColumnName("booking_id");
        b.Property(x => x.ServiceBookingId).HasColumnName("service_booking_id");
        b.Property(x => x.TravelerUserId).HasColumnName("traveler_user_id");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.PublicId).HasColumnName("public_id").HasMaxLength(255).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(16).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        b.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(10).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        b.Property(x => x.Extension).HasColumnName("extension").HasMaxLength(5).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        b.Property(x => x.InputByteLength).HasColumnName("input_byte_length");
        b.Property(x => x.StoredByteLength).HasColumnName("stored_byte_length");
        b.Property(x => x.Width).HasColumnName("width"); b.Property(x => x.Height).HasColumnName("height");
        b.Property(x => x.DeliveryUrl).HasColumnName("delivery_url").HasMaxLength(2048).UseCollation("Vietnamese_100_CI_AS");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.Property(x => x.UploadedAtUtc).HasColumnName("uploaded_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.Property(x => x.AdoptedAtUtc).HasColumnName("adopted_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.Property(x => x.CleanedAtUtc).HasColumnName("cleaned_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.Property(x => x.Version).HasColumnName("version").IsRowVersion();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.TravelerUserId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.PublicId).IsUnique().HasDatabaseName("UX_TripReviewMediaOperations_PublicId");
        b.HasIndex(x => new { x.BatchId, x.SortOrder }).IsUnique().HasDatabaseName("UX_TripReviewMediaOperations_BatchSlot");
        b.HasIndex(x => new { x.State, x.UpdatedAtUtc, x.Id }).HasDatabaseName("IX_TripReviewMediaOperations_Recovery");
        b.HasIndex(x => x.BookingId).HasFilter("[booking_id] IS NOT NULL").HasDatabaseName("IX_TripReviewMediaOperations_CommerceBooking");
        b.HasIndex(x => x.ServiceBookingId).HasFilter("[service_booking_id] IS NOT NULL").HasDatabaseName("IX_TripReviewMediaOperations_ServiceBooking");
    }
}
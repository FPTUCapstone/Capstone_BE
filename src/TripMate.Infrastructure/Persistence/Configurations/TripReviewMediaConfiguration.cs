using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TripReviewMediaConfiguration : IEntityTypeConfiguration<TripReviewMedia>
{
    public void Configure(EntityTypeBuilder<TripReviewMedia> b)
    {
        b.ToTable("TripReviewMedia", "social"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("media_id").ValueGeneratedOnAdd();
        b.Property(x => x.TripReviewId).HasColumnName("trip_review_id");
        b.Property(x => x.OperationId).HasColumnName("operation_id");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at").HasColumnType("datetime2(7)").AsUtcDateTime2();
        b.HasOne(x => x.Review).WithMany().HasForeignKey(x => x.TripReviewId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.Operation).WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.OperationId).IsUnique().HasDatabaseName("UX_TripReviewMedia_Operation");
        b.HasIndex(x => new { x.TripReviewId, x.SortOrder }).IsUnique().HasDatabaseName("UX_TripReviewMedia_ReviewSlot");
    }
}
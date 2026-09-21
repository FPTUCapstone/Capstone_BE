using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> builder)
    {
        builder.ToTable("Reviews", "social");

        builder.HasKey(review => review.Id);
        builder.Property(review => review.Id).HasColumnName("review_id").ValueGeneratedOnAdd();
        builder.Property(review => review.TravelerUserId).HasColumnName("traveler_user_id").IsRequired();
        builder.Property(review => review.TargetType)
            .HasColumnName("target_type")
            .HasMaxLength(Review.TargetTypeMaxLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(review => review.TargetId).HasColumnName("target_id").IsRequired();
        builder.Property(review => review.BookingId).HasColumnName("booking_id");
        builder.Property(review => review.Rating).HasColumnName("rating").IsRequired();
        builder.Property(review => review.ScenicRating).HasColumnName("scenic_rating");
        builder.Property(review => review.PhotoRating).HasColumnName("photo_rating");
        builder.Property(review => review.Comment)
            .HasColumnName("comment")
            .HasMaxLength(Review.CommentMaxLength);
        builder.Property(review => review.CreatedAtUtc)
            .HasColumnName("created_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(review => review.TravelerUserId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(review => new { review.TargetType, review.TargetId })
            .HasDatabaseName("IX_Reviews_Target");
    }
}
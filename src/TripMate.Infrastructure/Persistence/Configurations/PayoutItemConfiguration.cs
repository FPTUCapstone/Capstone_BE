using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PayoutItemConfiguration : IEntityTypeConfiguration<PayoutItem>
{
    public void Configure(EntityTypeBuilder<PayoutItem> builder)
    {
        builder.ToTable("PayoutItems", "payment");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("payout_item_id").ValueGeneratedOnAdd();
        builder.Property(item => item.PayoutId).HasColumnName("payout_id").IsRequired();
        builder.Property(item => item.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(item => item.Amount).HasColumnName("amount").HasPrecision(12, 2).IsRequired();
        builder.HasOne(item => item.Payout).WithMany()
            .HasForeignKey(item => item.PayoutId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Booking).WithMany()
            .HasForeignKey(item => item.BookingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.PayoutId, item.BookingId })
            .HasDatabaseName("UQ_PayoutItems")
            .IsUnique();
    }
}
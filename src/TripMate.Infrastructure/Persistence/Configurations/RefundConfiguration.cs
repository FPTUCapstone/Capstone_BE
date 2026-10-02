using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("Refunds", "payment");
        builder.HasKey(refund => refund.Id);
        builder.Property(refund => refund.Id).HasColumnName("refund_id").ValueGeneratedOnAdd();
        builder.Property(refund => refund.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(refund => refund.Reason).HasColumnName("reason").HasMaxLength(500);
        builder.Property(refund => refund.Amount).HasColumnName("amount").HasPrecision(12, 2).IsRequired();
        builder.Property(refund => refund.InitiatedBy).HasColumnName("initiated_by").HasMaxLength(14).IsUnicode(false).IsRequired();
        builder.Property(refund => refund.Status).HasColumnName("status").HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(refund => refund.RequestedAtUtc).HasColumnName("requested_at").AsUtcDateTime2().IsRequired();
        builder.Property(refund => refund.ProcessedAtUtc).HasColumnName("processed_at").AsUtcDateTime2();
        builder.HasOne(refund => refund.Booking).WithMany()
            .HasForeignKey(refund => refund.BookingId).OnDelete(DeleteBehavior.Restrict);
    }
}
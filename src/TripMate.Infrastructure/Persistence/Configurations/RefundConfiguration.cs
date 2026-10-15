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
        builder.Property(refund => refund.TransactionId).HasColumnName("transaction_id");
        builder.Property(refund => refund.CancellationOperationId).HasColumnName("cancellation_operation_id");
        builder.Property(refund => refund.Amount).HasColumnName("amount").HasPrecision(12, 2).IsRequired();
        builder.Property(refund => refund.Reason).HasColumnName("reason").HasMaxLength(Refund.ReasonMaxLength);
        builder.Property(refund => refund.InitiatedBy).HasColumnName("initiated_by").HasConversion<string>().HasMaxLength(14).IsUnicode(false).IsRequired();
        builder.Property(refund => refund.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(refund => refund.RequestedAtUtc).HasColumnName("requested_at").AsUtcDateTime2().IsRequired();
        builder.HasIndex(refund => refund.BookingId).HasDatabaseName("IX_Refunds_Booking");
        builder.HasIndex(refund => refund.CancellationOperationId).IsUnique()
            .HasFilter("[cancellation_operation_id] IS NOT NULL")
            .HasDatabaseName("UX_Refunds_CancellationOperation");
    }
}
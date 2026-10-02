using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PaymentTransactionConfiguration : IEntityTypeConfiguration<PaymentTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentTransaction> builder)
    {
        builder.ToTable("PaymentTransactions", "payment");
        builder.HasKey(transaction => transaction.Id);
        builder.Property(transaction => transaction.Id).HasColumnName("transaction_id").ValueGeneratedOnAdd();
        builder.Property(transaction => transaction.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(transaction => transaction.Gateway).HasColumnName("gateway").HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(transaction => transaction.GatewayTransactionRef).HasColumnName("gateway_transaction_ref").HasMaxLength(150);
        builder.Property(transaction => transaction.Amount).HasColumnName("amount").HasPrecision(12, 2).IsRequired();
        builder.Property(transaction => transaction.Currency).HasColumnName("currency").HasMaxLength(3).IsUnicode(false).IsRequired();
        builder.Property(transaction => transaction.TransactionType).HasColumnName("transaction_type").HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(transaction => transaction.Status).HasColumnName("status").HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(transaction => transaction.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.HasOne(transaction => transaction.Booking).WithMany()
            .HasForeignKey(transaction => transaction.BookingId).OnDelete(DeleteBehavior.Restrict);
    }
}
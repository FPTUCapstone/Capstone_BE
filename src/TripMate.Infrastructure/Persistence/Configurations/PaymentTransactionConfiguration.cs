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
        builder.Property(transaction => transaction.Amount).HasColumnName("amount").HasPrecision(12, 2).IsRequired();
        builder.Property(transaction => transaction.TransactionType).HasColumnName("transaction_type").HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(transaction => transaction.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(transaction => transaction.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.HasIndex(transaction => transaction.BookingId).HasDatabaseName("IX_PaymentTransactions_Booking");
    }
}
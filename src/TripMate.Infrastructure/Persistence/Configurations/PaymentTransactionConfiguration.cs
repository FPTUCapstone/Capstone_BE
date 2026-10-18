using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps payment.PaymentTransactions from database/tripmate_schema_v7.sql and UC-28 migration.
/// </summary>
public sealed class PaymentTransactionConfiguration : IEntityTypeConfiguration<PaymentTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentTransaction> builder)
    {
        builder.ToTable("PaymentTransactions", "payment");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasColumnName("transaction_id")
            .ValueGeneratedOnAdd();

        builder.Property(t => t.BookingId)
            .HasColumnName("booking_id")
            .IsRequired();

        builder.Property(t => t.Gateway)
            .HasColumnName("gateway")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(t => t.GatewayTransactionRef)
            .HasColumnName("gateway_transaction_ref")
            .HasMaxLength(150);

        builder.Property(t => t.Amount)
            .HasColumnName("amount")
            .HasPrecision(12, 2)
            .IsRequired();

        builder.Property(t => t.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(t => t.TransactionType)
            .HasColumnName("transaction_type")
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(t => t.Status)
            .HasColumnName("status")
            .HasMaxLength(10)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(t => t.OrderCode)
            .HasColumnName("order_code")
            .IsRequired();

        builder.Property(t => t.PaymentLinkId)
            .HasColumnName("payment_link_id")
            .HasMaxLength(100);

        builder.Property(t => t.CheckoutUrl)
            .HasColumnName("checkout_url")
            .HasMaxLength(1000);

        builder.Property(t => t.ExpiresAtUtc)
            .HasColumnName("expires_at")
            .AsUtcDateTime2();

        builder.Property(t => t.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .AsUtcDateTime2();

        builder.Property(t => t.RawResponse)
            .HasColumnName("raw_response");

        builder.Property(t => t.CreatedAtUtc)
            .HasColumnName("created_at")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasOne(t => t.Booking)
            .WithMany()
            .HasForeignKey(t => t.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => t.BookingId)
            .HasDatabaseName("IX_PaymentTransactions_Booking");

        builder.HasIndex(t => t.OrderCode)
            .IsUnique()
            .HasFilter("[order_code] > 0")
            .HasDatabaseName("UX_PaymentTransactions_OrderCode");

        builder.HasIndex(t => t.BookingId)
            .IsUnique()
            .HasFilter("[status] = 'Pending'")
            .HasDatabaseName("UX_PaymentTransactions_Booking_Pending");
    }
}
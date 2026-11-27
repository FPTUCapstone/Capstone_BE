using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class CommissionPaymentAttemptConfiguration : IEntityTypeConfiguration<CommissionPaymentAttempt>
{
    public void Configure(EntityTypeBuilder<CommissionPaymentAttempt> b)
    {
        b.ToTable("TourCommissionPaymentAttempts", "commercial");
        b.HasKey(p => p.Id); b.Property(p => p.Id).HasColumnName("payment_id").ValueGeneratedOnAdd();
        b.Property(p => p.StatementId).HasColumnName("statement_id");
        b.Property(p => p.OrderCode).HasColumnName("order_code");
        b.Property(p => p.Gateway).HasColumnName("gateway").HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        b.Property(p => p.AmountVnd).HasColumnName("amount_vnd");
        b.Property(p => p.Status).HasColumnName("status").HasMaxLength(20).IsUnicode(false);
        b.Property(p => p.PaymentLinkId).HasColumnName("payment_link_id").HasMaxLength(100).IsUnicode(false);
        b.Property(p => p.CheckoutUrl).HasColumnName("checkout_url").HasMaxLength(2000).IsUnicode(false);
        b.Property(p => p.GatewayReference).HasColumnName("gateway_reference").HasMaxLength(150).IsUnicode(false);
        b.Property(p => p.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        b.Property(p => p.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2();
        b.Property(p => p.ExpiresAtUtc).HasColumnName("expires_at").AsUtcDateTime2();
        b.Property(p => p.RowVersion).HasColumnName("row_version").IsRowVersion();
        b.HasOne<DirectCommissionStatement>().WithMany().HasForeignKey(p => p.StatementId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(p => p.OrderCode).IsUnique().HasDatabaseName("UX_TourCommissionPaymentAttempts_Order");
        b.HasIndex(p => p.StatementId).IsUnique().HasFilter("[status] IN ('Creating','Pending')").HasDatabaseName("UX_TourCommissionPaymentAttempts_Active");
        b.HasIndex(p => new { p.Gateway, p.GatewayReference }).IsUnique().HasFilter("[gateway_reference] IS NOT NULL").HasDatabaseName("UX_TourCommissionPaymentAttempts_Reference");
    }
}

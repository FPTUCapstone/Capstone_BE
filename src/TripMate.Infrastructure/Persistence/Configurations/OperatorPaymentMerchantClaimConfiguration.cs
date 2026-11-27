using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class OperatorPaymentMerchantClaimConfiguration : IEntityTypeConfiguration<OperatorPaymentMerchantClaim>
{
    public void Configure(EntityTypeBuilder<OperatorPaymentMerchantClaim> builder)
    {
        builder.ToTable("OperatorPaymentMerchantClaims", "payment");
        builder.HasKey(item => item.Id).HasName(OperatorPaymentMerchantClaim.KeyConstraintName);
        builder.Property(item => item.Id).HasColumnName("merchant_fingerprint")
            .HasMaxLength(OperatorPaymentMerchantClaim.FingerprintLength).IsUnicode(false);
        builder.Property(item => item.ChannelId).HasColumnName("channel_id");
        builder.Property(item => item.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.HasOne(item => item.Channel).WithMany().HasForeignKey(item => item.ChannelId).OnDelete(DeleteBehavior.Restrict);
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class VoucherConfiguration : IEntityTypeConfiguration<Voucher>
{
    public void Configure(EntityTypeBuilder<Voucher> builder)
    {
        builder.ToTable("Vouchers", "commerce");
        builder.HasKey(voucher => voucher.Id);
        builder.Property(voucher => voucher.Id).HasColumnName("voucher_id").ValueGeneratedOnAdd();
        builder.Property(voucher => voucher.OwnerOperatorUserId).HasColumnName("owner_operator_user_id");
        builder.Property(voucher => voucher.Code).HasColumnName("code").HasMaxLength(Voucher.CodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(voucher => voucher.DiscountType).HasColumnName("discount_type").HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(voucher => voucher.DiscountValue).HasColumnName("discount_value").HasPrecision(12, 2).IsRequired();
        builder.Property(voucher => voucher.MaxDiscountAmount).HasColumnName("max_discount_amount").HasPrecision(12, 2);
        builder.Property(voucher => voucher.MinOrderAmount).HasColumnName("min_order_amount").HasPrecision(12, 2).IsRequired();
        builder.Property(voucher => voucher.UsageLimit).HasColumnName("usage_limit");
        builder.Property(voucher => voucher.UsageLimitPerUser).HasColumnName("usage_limit_per_user");
        builder.Property(voucher => voucher.UsedCount).HasColumnName("used_count").IsRequired();
        builder.Property(voucher => voucher.ValidFromUtc).HasColumnName("valid_from").AsUtcDateTime2().IsRequired();
        builder.Property(voucher => voucher.ValidToUtc).HasColumnName("valid_to").AsUtcDateTime2().IsRequired();
        builder.Property(voucher => voucher.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(voucher => voucher.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.HasIndex(voucher => voucher.Code).IsUnique();
        builder.HasOne<OperatorProfile>().WithMany().HasForeignKey(voucher => voucher.OwnerOperatorUserId).OnDelete(DeleteBehavior.NoAction);
        builder.Navigation(voucher => voucher.ApplicableTours).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
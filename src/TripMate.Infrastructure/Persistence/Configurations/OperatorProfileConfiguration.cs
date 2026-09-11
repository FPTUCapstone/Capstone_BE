using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class OperatorProfileConfiguration : IEntityTypeConfiguration<OperatorProfile>
{
    public void Configure(EntityTypeBuilder<OperatorProfile> builder)
    {
        builder.ToTable("OperatorProfiles", "dbo");

        builder.HasKey(op => op.UserId);
        builder.Property(op => op.UserId).HasColumnName("user_id").ValueGeneratedNever();

        builder.Property(op => op.CompanyName).HasColumnName("company_name").HasMaxLength(200).IsRequired();
        builder.Property(op => op.TaxCode).HasColumnName("tax_code").HasMaxLength(50).IsRequired();
        builder.Property(op => op.BusinessLicenseNo).HasColumnName("business_license_no").HasMaxLength(100).IsRequired();
        builder.Property(op => op.ContactPhone).HasColumnName("contact_phone").HasMaxLength(20);
        builder.Property(op => op.ContactAddress).HasColumnName("contact_address").HasMaxLength(300);
        builder.Property(op => op.CommissionRate).HasColumnName("commission_rate").HasPrecision(5, 2);
        builder.Property(op => op.ApprovalStatus).HasColumnName("approval_status").HasConversion<string>().HasMaxLength(20).IsConcurrencyToken();
        builder.Property(op => op.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(500);
        builder.Property(op => op.ReviewedBy).HasColumnName("reviewed_by");
        builder.Property(op => op.ReviewedAtUtc).HasColumnName("reviewed_at").AsUtcDateTime2();
        builder.Property(op => op.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.Property(op => op.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2();

        builder.HasIndex(op => op.TaxCode).IsUnique().HasDatabaseName("UQ_OperatorProfiles_TaxCode");

        builder
            .HasOne(op => op.User)
            .WithOne()
            .HasForeignKey<OperatorProfile>(op => op.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(op => op.Reviewer)
            .WithMany()
            .HasForeignKey(op => op.ReviewedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasMany(op => op.Documents)
            .WithOne(d => d.OperatorProfile)
            .HasForeignKey(d => d.OperatorUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

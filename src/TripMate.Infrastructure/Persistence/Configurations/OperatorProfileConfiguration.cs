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
        builder.Property(op => op.RejectionReason).HasColumnName("rejection_reason")
            .HasMaxLength(OperatorProfile.RejectionReasonMaxLength);
        builder.Property(op => op.ReviewedBy).HasColumnName("reviewed_by");
        builder.Property(op => op.ReviewedAtUtc).HasColumnName("reviewed_at").AsUtcDateTime2();
        builder.Property(op => op.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.Property(op => op.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2();

        builder.Property(op => op.Description).HasColumnName("description").HasMaxLength(OperatorProfile.DescriptionMaxLength);
        builder.Property(op => op.ContactEmail).HasColumnName("contact_email").HasMaxLength(OperatorProfile.ContactEmailMaxLength);
        builder.Property(op => op.WebsiteUrl).HasColumnName("website_url").HasMaxLength(OperatorProfile.WebsiteUrlMaxLength);
        builder.Property(op => op.LogoUrl).HasColumnName("logo_url").HasMaxLength(OperatorProfile.LogoUrlMaxLength);
        builder.Property(op => op.LogoPublicId).HasColumnName("logo_public_id").HasMaxLength(OperatorProfile.LogoPublicIdMaxLength);

        builder.Property(op => op.BankName).HasColumnName("bank_name").HasMaxLength(OperatorProfile.BankNameMaxLength);
        builder.Property(op => op.AccountHolderName).HasColumnName("account_holder_name")
            .HasMaxLength(OperatorProfile.AccountHolderNameMaxLength);
        builder.Property(op => op.AccountNumberMasked).HasColumnName("account_number_masked")
            .HasMaxLength(OperatorProfile.AccountNumberMaskedMaxLength);
        builder.Property(op => op.AccountNumberProtected).HasColumnName("account_number_protected").HasMaxLength(500);
        builder.Property(op => op.BankAccountUpdatedAtUtc).HasColumnName("bank_account_updated_at").AsUtcDateTime2();

        builder.HasIndex(op => op.TaxCode).IsUnique().HasDatabaseName("UQ_OperatorProfiles_TaxCode");
        builder.HasIndex(op => op.BusinessLicenseNo)
            .IsUnique()
            .HasDatabaseName("UX_OperatorProfiles_BusinessLicenseNo")
            .HasFilter("[business_license_no] <> N''");

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
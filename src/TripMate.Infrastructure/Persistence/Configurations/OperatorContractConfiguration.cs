using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class OperatorContractConfiguration : IEntityTypeConfiguration<OperatorContract>
{
    public void Configure(EntityTypeBuilder<OperatorContract> builder)
    {
        builder.ToTable("OperatorContracts", "admin");
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.Id, x.OperatorUserId }).HasName("AK_OperatorContracts_ContractOwner");
        builder.Property(x => x.Id).HasColumnName("contract_id").ValueGeneratedOnAdd();
        builder.Property(x => x.OperatorUserId).HasColumnName("operator_user_id");
        builder.Property(x => x.TemplateId).HasColumnName("template_id");
        builder.Property(x => x.TemplateVersion).HasColumnName("template_version");
        builder.Property(x => x.TemplateSourceStorageKey).HasColumnName("template_source_storage_key").HasMaxLength(500);
        builder.Property(x => x.TemplateSourceSha256).HasColumnName("template_source_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(200);
        builder.Property(x => x.CompanyName).HasColumnName("company_name").HasMaxLength(200);
        builder.Property(x => x.TaxCode).HasColumnName("tax_code").HasMaxLength(50);
        builder.Property(x => x.BusinessLicenseNo).HasColumnName("business_license_no").HasMaxLength(100);
        builder.Property(x => x.BusinessAddress).HasColumnName("business_address").HasMaxLength(300);
        builder.Property(x => x.OperatorEmail).HasColumnName("operator_email").HasMaxLength(256);
        builder.Property(x => x.SignerName).HasColumnName("signer_name").HasMaxLength(150);
        builder.Property(x => x.SignerTitle).HasColumnName("signer_title").HasMaxLength(150);
        builder.Property(x => x.SignerCapacity).HasColumnName("signer_capacity").HasMaxLength(500);
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(x => x.DocumentState).HasColumnName("document_state").HasConversion<string>().HasMaxLength(24).IsUnicode(false);
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.SnapshotSha256).HasColumnName("snapshot_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.UnsignedDocumentStorageKey).HasColumnName("unsigned_document_storage_key").HasMaxLength(500);
        builder.Property(x => x.UnsignedDocumentSha256).HasColumnName("unsigned_document_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.SignatureAnchorsJson).HasColumnName("signature_anchors_json").HasMaxLength(16000);
        builder.Property(x => x.SignedDocumentStorageKey).HasColumnName("signed_document_storage_key").HasMaxLength(500);
        builder.Property(x => x.SignedDocumentSha256).HasColumnName("signed_document_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.SignatureStorageKey).HasColumnName("signature_storage_key").HasMaxLength(500);
        builder.Property(x => x.SignatureSha256).HasColumnName("signature_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
        builder.Property(x => x.ApprovedAtUtc).HasColumnName("approved_at").AsUtcDateTime2();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2();
        builder.Property(x => x.SignedAtUtc).HasColumnName("signed_at").AsUtcDateTime2();
        builder.Property(x => x.VoidedAtUtc).HasColumnName("voided_at").AsUtcDateTime2();
        builder.Property(x => x.PreparationClaimToken).HasColumnName("preparation_claim_token");
        builder.Property(x => x.PreparationLeaseExpiresAtUtc).HasColumnName("preparation_lease_expires_at").AsUtcDateTime2();
        builder.Property(x => x.PreparationAttempts).HasColumnName("preparation_attempts");
        builder.Property(x => x.PreparationErrorCode).HasColumnName("preparation_error_code").HasMaxLength(100).IsUnicode(false);
        builder.Property(x => x.CurrentSigningChallengeId).HasColumnName("current_signing_challenge_id");
        builder.Property(x => x.LastOtpIssuedAtUtc).HasColumnName("last_otp_issued_at").AsUtcDateTime2();
        builder.Property(x => x.SigningOperationId).HasColumnName("signing_operation_id");
        builder.Property(x => x.SigningRequestSha256).HasColumnName("signing_request_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.ConsentVersion).HasColumnName("consent_version").HasMaxLength(80);
        builder.Property(x => x.RowVersion).HasColumnName("row_version").IsRowVersion();
        builder.HasIndex(x => x.OperatorUserId).IsUnique().HasFilter("[status] <> 'Voided'").HasDatabaseName("UX_OperatorContracts_Current");
        builder.HasIndex(x => new { x.OperatorUserId, x.SigningOperationId }).IsUnique().HasFilter("[signing_operation_id] IS NOT NULL").HasDatabaseName("UX_OperatorContracts_SigningOperation");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OperatorContractTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ContractTemplateRenderJobConfiguration : IEntityTypeConfiguration<ContractTemplateRenderJob>
{
    public void Configure(EntityTypeBuilder<ContractTemplateRenderJob> builder)
    {
        builder.ToTable("ContractTemplateRenderJobs", "admin");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("job_id").ValueGeneratedOnAdd();
        builder.Property(x => x.SourceSha256).HasColumnName("source_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.FailureCode).HasColumnName("failure_code").HasMaxLength(100).IsUnicode(false);
        builder.Property(x => x.TemplateId).HasColumnName("template_id");
        builder.Property(x => x.Revision).HasColumnName("revision");
        builder.Property(x => x.ClaimToken).HasColumnName("claim_token");
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        builder.Property(x => x.RequestedAtUtc).HasColumnName("requested_at").AsUtcDateTime2();
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at").AsUtcDateTime2();
        builder.Property(x => x.LeaseExpiresAtUtc).HasColumnName("lease_expires_at").AsUtcDateTime2();
        builder.Property(x => x.RowVersion).HasColumnName("row_version").IsRowVersion();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(12).IsUnicode(false);
        builder.HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.TemplateId, x.Revision }).IsUnique().HasDatabaseName("UX_ContractTemplateRenderJobs_Revision");
        builder.HasIndex(x => new { x.Status, x.LeaseExpiresAtUtc }).HasDatabaseName("IX_ContractTemplateRenderJobs_Claim");
    }
}
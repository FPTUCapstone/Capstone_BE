using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class OperatorContractSigningChallengeConfiguration : IEntityTypeConfiguration<OperatorContractSigningChallenge>
{
    public void Configure(EntityTypeBuilder<OperatorContractSigningChallenge> builder)
    {
        builder.ToTable("OperatorContractSigningChallenges", "admin");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("challenge_id").ValueGeneratedNever();
        builder.Property(x => x.ContractId).HasColumnName("contract_id");
        builder.Property(x => x.OperatorUserId).HasColumnName("operator_user_id");
        builder.Property(x => x.AcceptedContentSha256).HasColumnName("accepted_content_sha256").HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.ProtectedOtp).HasColumnName("protected_otp").HasMaxLength(4000);
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at").AsUtcDateTime2();
        builder.Property(x => x.SentAtUtc).HasColumnName("sent_at").AsUtcDateTime2();
        builder.Property(x => x.UsedAtUtc).HasColumnName("used_at").AsUtcDateTime2();
        builder.Property(x => x.FailedAttempts).HasColumnName("failed_attempts");
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(12).IsUnicode(false);
        builder.Property(x => x.RowVersion).HasColumnName("row_version").IsRowVersion();
        builder.HasIndex(x => new { x.OperatorUserId, x.CreatedAtUtc });
        builder.HasOne<OperatorContract>().WithMany().HasForeignKey(x => new { x.ContractId, x.OperatorUserId })
            .HasPrincipalKey(x => new { x.Id, x.OperatorUserId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OperatorUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
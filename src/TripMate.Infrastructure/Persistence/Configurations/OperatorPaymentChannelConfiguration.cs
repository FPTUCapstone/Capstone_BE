using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class OperatorPaymentChannelConfiguration : IEntityTypeConfiguration<OperatorPaymentChannel>
{
    public void Configure(EntityTypeBuilder<OperatorPaymentChannel> builder)
    {
        builder.ToTable("OperatorPaymentChannels", "payment");
        builder.HasKey(ch => ch.Id);
        builder.Property(ch => ch.Id).HasColumnName("channel_id").ValueGeneratedOnAdd();
        builder.Property(ch => ch.TourOperatorUserId).HasColumnName("tour_operator_user_id").IsRequired();
        builder.Property(ch => ch.OpaqueId).HasColumnName("opaque_id").IsRequired();
        builder.Property(ch => ch.Status).HasColumnName("status").HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(ch => ch.ChannelVersion).HasColumnName("channel_version").IsRequired();
        builder.Property(ch => ch.ClientIdRef).HasColumnName("client_id_ref")
            .HasMaxLength(OperatorPaymentChannel.ClientIdRefMaxLength);
        builder.Property(ch => ch.ApiKeyRef).HasColumnName("api_key_ref")
            .HasMaxLength(OperatorPaymentChannel.ApiKeyRefMaxLength);
        builder.Property(ch => ch.ChecksumKeyRef).HasColumnName("checksum_key_ref")
            .HasMaxLength(OperatorPaymentChannel.ChecksumKeyRefMaxLength);
        builder.Property(ch => ch.BeneficiaryAccountNumber).HasColumnName("beneficiary_account_number")
            .HasMaxLength(OperatorPaymentChannel.BeneficiaryAccountNumberMaxLength);
        builder.Property(ch => ch.BeneficiaryAccountName).HasColumnName("beneficiary_account_name")
            .HasMaxLength(OperatorPaymentChannel.BeneficiaryAccountNameMaxLength);
        builder.Property(ch => ch.BeneficiaryBankCode).HasColumnName("beneficiary_bank_code")
            .HasMaxLength(OperatorPaymentChannel.BeneficiaryBankCodeMaxLength).IsUnicode(false);
        builder.Property(ch => ch.WebhookRegisteredAtUtc).HasColumnName("webhook_registered_at").AsUtcDateTime2();
        builder.Property(ch => ch.WebhookUrl).HasColumnName("webhook_url")
            .HasMaxLength(OperatorPaymentChannel.WebhookUrlMaxLength);
        builder.Property(ch => ch.ConnectedAtUtc).HasColumnName("connected_at").AsUtcDateTime2();
        builder.Property(ch => ch.LastVerifiedAtUtc).HasColumnName("last_verified_at").AsUtcDateTime2();
        builder.Property(ch => ch.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(ch => ch.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2().IsRequired();
        builder.Property(ch => ch.RowVersion).HasColumnName("row_version").IsRowVersion();

        builder.HasOne<User>().WithMany()
            .HasForeignKey(ch => ch.TourOperatorUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(ch => ch.OpaqueId).IsUnique().HasDatabaseName("UQ_OperatorPaymentChannels_OpaqueId");
        builder.HasIndex(ch => ch.TourOperatorUserId).IsUnique()
            .HasDatabaseName("UQ_OperatorPaymentChannels_Operator");
        builder.HasIndex(ch => ch.Status).HasDatabaseName("IX_OperatorPaymentChannels_Status");
    }
}

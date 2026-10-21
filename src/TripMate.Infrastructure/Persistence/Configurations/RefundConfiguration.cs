using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("Refunds", "payment");
        builder.HasKey(refund => refund.Id);
        builder.Property(refund => refund.Id).HasColumnName("refund_id").ValueGeneratedOnAdd();
        builder.Property(refund => refund.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(refund => refund.TransactionId).HasColumnName("transaction_id");
        builder.Property(refund => refund.CancellationOperationId).HasColumnName("cancellation_operation_id");
        builder.Property(refund => refund.Amount).HasColumnName("amount").HasPrecision(12, 2).IsRequired();
        builder.Property(refund => refund.Reason).HasColumnName("reason").HasMaxLength(Refund.ReasonMaxLength);
        builder.Property(refund => refund.InitiatedBy).HasColumnName("initiated_by").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(refund => refund.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(refund => refund.RequestedAtUtc).HasColumnName("requested_at").IsRequired();
        builder.Property(refund => refund.ProcessedAtUtc).HasColumnName("processed_at");
        builder.Property(refund => refund.GatewayReference).HasColumnName("gateway_reference")
            .HasMaxLength(Refund.GatewayReferenceMaxLength);
        builder.Property(refund => refund.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(refund => refund.LastAttemptAtUtc).HasColumnName("last_attempt_at");
        builder.Property(refund => refund.NextAttemptAtUtc).HasColumnName("next_attempt_at");
        builder.Property(refund => refund.LastErrorCode).HasColumnName("last_error_code")
            .HasMaxLength(Refund.ErrorCodeMaxLength);
        builder.Property(refund => refund.LastErrorMessage).HasColumnName("last_error_message")
            .HasMaxLength(Refund.ErrorMessageMaxLength);

        builder.HasIndex(refund => new { refund.Status, refund.NextAttemptAtUtc })
            .HasDatabaseName("IX_Refunds_Due");

        builder.HasOne(refund => refund.Booking)
            .WithMany()
            .HasForeignKey(refund => refund.BookingId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
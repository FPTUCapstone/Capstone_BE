using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class EmergencyTourCancellationOperationConfiguration
    : IEntityTypeConfiguration<EmergencyTourCancellationOperation>
{
    public void Configure(EntityTypeBuilder<EmergencyTourCancellationOperation> builder)
    {
        builder.ToTable("EmergencyTourCancellationOperations", "commerce");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).HasColumnName("operation_id").ValueGeneratedOnAdd();
        builder.Property(operation => operation.TourScheduleId).HasColumnName("tour_schedule_id").IsRequired();
        builder.Property(operation => operation.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(operation => operation.CommandId).HasColumnName("command_id").IsRequired();
        builder.Property(operation => operation.RequestHash).HasColumnName("request_hash")
            .HasMaxLength(EmergencyTourCancellationOperation.HashMaxLength).IsUnicode(false).IsRequired();
        builder.Property(operation => operation.Action).HasColumnName("action")
            .HasMaxLength(EmergencyTourCancellationOperation.ActionMaxLength).IsUnicode(false).IsRequired();
        builder.Property(operation => operation.ReasonCategory).HasColumnName("reason_category")
            .HasMaxLength(EmergencyTourCancellationOperation.ReasonCategoryMaxLength).IsUnicode(false).IsRequired();
        builder.Property(operation => operation.ReasonDetails).HasColumnName("reason_details")
            .HasMaxLength(EmergencyTourCancellationOperation.ReasonDetailsMaxLength).IsRequired();
        builder.Property(operation => operation.ImmediateInstructions).HasColumnName("immediate_instructions")
            .HasMaxLength(EmergencyTourCancellationOperation.InstructionsMaxLength);
        builder.Property(operation => operation.EmergencyContact).HasColumnName("emergency_contact")
            .HasMaxLength(EmergencyTourCancellationOperation.ContactMaxLength).IsRequired(false);
        builder.Property(operation => operation.EvidenceReference).HasColumnName("evidence_reference")
            .HasMaxLength(EmergencyTourCancellationOperation.EvidenceMaxLength);
        builder.Property(operation => operation.EvidenceUrl).HasColumnName("evidence_url")
            .HasMaxLength(EmergencyTourCancellationOperation.EvidenceUrlMaxLength);
        builder.Property(operation => operation.AuthorityReference).HasColumnName("authority_reference")
            .HasMaxLength(EmergencyTourCancellationOperation.AuthorityReferenceMaxLength);
        builder.Property(operation => operation.EvidenceProvenance).HasColumnName("evidence_provenance")
            .HasMaxLength(EmergencyTourCancellationOperation.EvidenceProvenanceMaxLength)
            .IsUnicode(false).IsRequired();
        builder.Property(operation => operation.EvidenceAddedLater).HasColumnName("evidence_added_later")
            .IsRequired();
        builder.Property(operation => operation.ContractPolicySnapshot).HasColumnName("contract_policy_snapshot")
            .HasMaxLength(EmergencyTourCancellationOperation.ContractPolicySnapshotMaxLength).IsRequired();
        builder.Property(operation => operation.AffectedBookingCount).HasColumnName("affected_booking_count").IsRequired();
        builder.Property(operation => operation.CommittedAtUtc).HasColumnName("committed_at")
            .AsUtcDateTime2().IsRequired();
        builder.HasOne(operation => operation.TourSchedule).WithMany()
            .HasForeignKey(operation => operation.TourScheduleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(operation => operation.ActorUserId)
            .IsRequired().OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(operation => operation.TourScheduleId).IsUnique()
            .HasDatabaseName("UX_EmergencyTourCancellationOperations_Schedule");
        builder.HasIndex(operation => new { operation.ActorUserId, operation.CommandId }).IsUnique()
            .HasDatabaseName("UX_EmergencyTourCancellationOperations_ActorCommand");
    }
}

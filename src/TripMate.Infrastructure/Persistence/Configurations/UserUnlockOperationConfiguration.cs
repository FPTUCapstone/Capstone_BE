using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class UserUnlockOperationConfiguration : IEntityTypeConfiguration<UserUnlockOperation>
{
    public void Configure(EntityTypeBuilder<UserUnlockOperation> builder)
    {
        builder.ToTable("UserUnlockOperations", "admin");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id)
            .HasColumnName("operation_id")
            .ValueGeneratedOnAdd();
        builder.Property(operation => operation.AdministratorUserId)
            .HasColumnName("administrator_user_id")
            .IsRequired();
        builder.Property(operation => operation.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(operation => operation.RequestHash)
            .HasColumnName("request_hash")
            .HasMaxLength(128)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(operation => operation.TargetUserId)
            .HasColumnName("target_user_id")
            .IsRequired();
        builder.Property(operation => operation.RestoredStatus)
            .HasColumnName("restored_status")
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(operation => operation.UnlockedAtUtc)
            .HasColumnName("unlocked_at_utc")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasIndex(operation => new { operation.AdministratorUserId, operation.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_UserUnlockOperations_AdministratorKey");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(operation => operation.AdministratorUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(operation => operation.TargetUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ServiceProviderRequestOperationConfiguration : IEntityTypeConfiguration<ServiceProviderRequestOperation>
{
    public void Configure(EntityTypeBuilder<ServiceProviderRequestOperation> builder)
    {
        builder.ToTable("ServiceProviderRequestOperations", "commercial");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).HasColumnName("operation_id").ValueGeneratedOnAdd();
        builder.Property(operation => operation.ServiceBookingId).HasColumnName("service_booking_id");
        builder.Property(operation => operation.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(operation => operation.IdempotencyKey).HasColumnName("idempotency_key");
        builder.Property(operation => operation.OperationType).HasColumnName("operation_type").HasMaxLength(10).IsUnicode(false);
        builder.Property(operation => operation.RequestHash).HasColumnName("request_hash").HasColumnType("char(64)");
        builder.Property(operation => operation.ResponseJson).HasColumnName("response_json").IsRequired();
        builder.Property(operation => operation.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.HasOne(operation => operation.ServiceBooking).WithMany().HasForeignKey(operation => operation.ServiceBookingId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(operation => operation.ActorUserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(operation => new { operation.ActorUserId, operation.IdempotencyKey }).IsUnique()
            .HasDatabaseName("UX_ServiceProviderRequestOperations_ActorKey");
    }
}
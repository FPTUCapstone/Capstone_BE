using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class BookingCancellationOperationConfiguration : IEntityTypeConfiguration<BookingCancellationOperation>
{
    public void Configure(EntityTypeBuilder<BookingCancellationOperation> builder)
    {
        builder.ToTable("BookingCancellationOperations", "commerce");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).HasColumnName("operation_id").ValueGeneratedOnAdd();
        builder.Property(operation => operation.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(operation => operation.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(operation => operation.Actor).HasColumnName("actor").HasConversion<string>().HasMaxLength(12).IsUnicode(false).IsRequired();
        builder.Property(operation => operation.IdempotencyKey).HasColumnName("idempotency_key").IsRequired();
        builder.Property(operation => operation.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(operation => operation.RefundAmount).HasColumnName("refund_amount").HasPrecision(12, 2).IsRequired();
        builder.Property(operation => operation.CancelledAtUtc).HasColumnName("cancelled_at").AsUtcDateTime2().IsRequired();
        builder.HasIndex(operation => new { operation.ActorUserId, operation.IdempotencyKey }).IsUnique().HasDatabaseName("UX_BookingCancellationOperations_ActorKey");
        builder.HasIndex(operation => operation.BookingId).HasDatabaseName("IX_BookingCancellationOperations_Booking");
    }
}

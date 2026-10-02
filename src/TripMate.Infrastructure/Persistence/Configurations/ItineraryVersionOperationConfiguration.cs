using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ItineraryVersionOperationConfiguration
    : IEntityTypeConfiguration<ItineraryVersionOperation>
{
    public void Configure(EntityTypeBuilder<ItineraryVersionOperation> builder)
    {
        builder.ToTable("ItineraryVersionOperations", "planning");
        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id)
            .HasColumnName("operation_id")
            .ValueGeneratedOnAdd();
        builder.Property(operation => operation.TravelerUserId)
            .HasColumnName("traveler_user_id")
            .IsRequired();
        builder.Property(operation => operation.SourceItineraryId)
            .HasColumnName("source_itinerary_id")
            .IsRequired();
        builder.Property(operation => operation.OperationType)
            .HasColumnName("operation_type")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(operation => operation.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .IsRequired();
        builder.Property(operation => operation.RequestHash)
            .HasColumnName("request_hash")
            .HasMaxLength(128)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(operation => operation.ResultItineraryId)
            .HasColumnName("result_itinerary_id");
        builder.Property(operation => operation.CreatedAtUtc)
            .HasColumnName("created_at")
            .AsUtcDateTime2()
            .IsRequired();

        builder.HasIndex(operation => new { operation.TravelerUserId, operation.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_ItineraryVersionOperations_TravelerKey");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(operation => operation.TravelerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Itinerary>()
            .WithMany()
            .HasForeignKey(operation => operation.SourceItineraryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Itinerary>()
            .WithMany()
            .HasForeignKey(operation => operation.ResultItineraryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
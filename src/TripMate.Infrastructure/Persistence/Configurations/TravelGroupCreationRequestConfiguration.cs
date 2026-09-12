using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TravelGroupCreationRequestConfiguration
    : IEntityTypeConfiguration<TravelGroupCreationRequest>
{
    public void Configure(EntityTypeBuilder<TravelGroupCreationRequest> builder)
    {
        builder.ToTable("TravelGroupCreationRequests", "social");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id)
            .HasColumnName("request_id")
            .ValueGeneratedOnAdd();
        builder.Property(request => request.TravelerUserId)
            .HasColumnName("traveler_user_id")
            .IsRequired();
        builder.Property(request => request.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .IsRequired();
        builder.Property(request => request.TravelGroupId)
            .HasColumnName("group_id")
            .IsRequired();
        builder.Property(request => request.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();
        builder.HasIndex(request => new { request.TravelerUserId, request.IdempotencyKey })
            .IsUnique();
        builder.HasOne(request => request.TravelGroup)
            .WithMany()
            .HasForeignKey(request => request.TravelGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

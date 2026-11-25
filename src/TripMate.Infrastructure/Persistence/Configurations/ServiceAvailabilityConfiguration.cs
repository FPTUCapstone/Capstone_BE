using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ServiceAvailabilityConfiguration : IEntityTypeConfiguration<ServiceAvailability>
{
    public void Configure(EntityTypeBuilder<ServiceAvailability> builder)
    {
        builder.ToTable("ServiceAvailability", "commercial");
        builder.HasKey(slot => new { slot.ServiceId, slot.StartsAtUtc });
        builder.Property(slot => slot.ServiceId).HasColumnName("service_id");
        builder.Property(slot => slot.StartsAtUtc).HasColumnName("starts_at").AsUtcDateTime2();
        builder.Property(slot => slot.Quantity).HasColumnName("quantity");
        builder.HasOne<CommercialService>().WithMany().HasForeignKey(slot => slot.ServiceId).OnDelete(DeleteBehavior.NoAction);
    }
}

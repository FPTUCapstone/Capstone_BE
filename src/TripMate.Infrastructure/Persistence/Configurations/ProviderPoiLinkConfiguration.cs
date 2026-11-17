using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ProviderPoiLinkConfiguration : IEntityTypeConfiguration<ProviderPoiLink>
{
    public void Configure(EntityTypeBuilder<ProviderPoiLink> builder)
    {
        builder.ToTable("ProviderPoiLinks", "commercial");
        builder.HasKey(link => new { link.ServiceProviderId, link.PointOfInterestId })
            .HasName("PK_ProviderPoiLinks");
        builder.Property(link => link.ServiceProviderId).HasColumnName("provider_id");
        builder.Property(link => link.PointOfInterestId).HasColumnName("poi_id");
        builder.HasOne(link => link.Provider).WithMany(provider => provider.PoiLinks)
            .HasForeignKey(link => link.ServiceProviderId).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_ProviderPoiLinks_Provider");
        builder.HasOne(link => link.Poi).WithMany().HasForeignKey(link => link.PointOfInterestId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_ProviderPoiLinks_Poi");
        builder.HasIndex(link => link.PointOfInterestId).IsUnique().HasDatabaseName("UX_ProviderPoiLinks_Poi");
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class SystemConfigConfiguration : IEntityTypeConfiguration<SystemConfig>
{
    public void Configure(EntityTypeBuilder<SystemConfig> builder)
    {
        builder.ToTable("SystemConfigs", "dbo");

        builder.HasKey(config => config.ConfigKey);
        builder.Property(config => config.ConfigKey)
            .HasColumnName("config_key")
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(config => config.ConfigValue)
            .HasColumnName("config_value")
            .HasMaxLength(500)
            .IsRequired();
        builder.Property(config => config.Description)
            .HasColumnName("description")
            .HasMaxLength(500);
        builder.Property(config => config.UpdatedBy).HasColumnName("updated_by");
        builder.Property(config => config.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .AsUtcDateTime2();
    }
}
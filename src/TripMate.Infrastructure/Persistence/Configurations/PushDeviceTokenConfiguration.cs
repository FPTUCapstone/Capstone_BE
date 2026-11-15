using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class PushDeviceTokenConfiguration : IEntityTypeConfiguration<PushDeviceToken>
{
    public void Configure(EntityTypeBuilder<PushDeviceToken> builder)
    {
        builder.ToTable("PushDeviceTokens", "dbo");
        builder.HasKey(device => device.Id);
        builder.Property(device => device.Id).HasColumnName("push_device_token_id").ValueGeneratedOnAdd();
        builder.Property(device => device.InstallationId).HasColumnName("installation_id").IsRequired();
        builder.Property(device => device.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(device => device.ProtectedToken).HasColumnName("protected_token")
            .HasMaxLength(PushDeviceToken.ProtectedTokenMaxLength).IsRequired();
        builder.Property(device => device.TokenHash).HasColumnName("token_hash")
            .HasMaxLength(PushDeviceToken.TokenHashLength).IsUnicode(false).IsFixedLength().IsRequired();
        builder.Property(device => device.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();
        builder.Property(device => device.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2().IsRequired();
        builder.Property(device => device.DeactivatedAtUtc).HasColumnName("deactivated_at").AsUtcDateTime2();
        builder.HasOne(device => device.User).WithMany().HasForeignKey(device => device.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(device => device.InstallationId).IsUnique()
            .HasDatabaseName("UQ_PushDeviceTokens_Installation");
        builder.HasIndex(device => device.TokenHash).IsUnique()
            .HasDatabaseName("UQ_PushDeviceTokens_TokenHash");
        builder.HasIndex(device => new { device.UserId, device.DeactivatedAtUtc })
            .HasDatabaseName("IX_PushDeviceTokens_User_Active");
    }
}
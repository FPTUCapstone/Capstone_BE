using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>Maps dbo.RefreshTokens exactly as defined in database/tripmate_schema_v6.sql — see
/// the note on UserConfiguration about this project being database-first.</summary>
public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens", "dbo");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("refresh_token_id").ValueGeneratedOnAdd();

        builder.Property(t => t.UserId).HasColumnName("user_id");
        builder.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(500).IsRequired();
        builder.Property(t => t.ExpiresAtUtc).HasColumnName("expires_at").AsUtcDateTime2().IsRequired();
        builder.Property(t => t.RevokedAtUtc).HasColumnName("revoked_at").AsUtcDateTime2();
        builder.Property(t => t.DeviceInfo).HasColumnName("device_info").HasMaxLength(300);
        builder.Property(t => t.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();

        builder.Ignore(t => t.IsActive);

        builder.HasIndex(t => t.UserId).HasDatabaseName("IX_RefreshTokens_User");
    }
}
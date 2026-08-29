using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>Maps dbo.Users exactly as defined in database/tripmate_schema_v6.sql — this project
/// is database-first: the table already exists (applied via database/apply-schema.sh), EF Core
/// never creates or migrates it. Keep this in sync by hand whenever the .sql file changes.</summary>
public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", "dbo");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("user_id").ValueGeneratedOnAdd();

        builder.Property(u => u.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(256);
        builder.Property(u => u.PhoneNumber).HasColumnName("phone_number").HasMaxLength(20);
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(256);
        builder.Property(u => u.FullName).HasColumnName("full_name").HasMaxLength(150).IsRequired();
        builder.Property(u => u.AvatarUrl).HasColumnName("avatar_url").HasMaxLength(500);
        builder.Property(u => u.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(24);
        builder.Property(u => u.EmailVerifiedAtUtc).HasColumnName("email_verified_at").AsUtcDateTime2();
        builder.Property(u => u.PhoneVerifiedAtUtc).HasColumnName("phone_verified_at").AsUtcDateTime2();
        builder.Property(u => u.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        builder.Property(u => u.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2();
        builder.Property(u => u.LastLoginAtUtc).HasColumnName("last_login_at").AsUtcDateTime2();

        builder.HasIndex(u => u.Email).IsUnique().HasFilter("[email] IS NOT NULL")
            .HasDatabaseName("UX_Users_Email");
        builder.HasIndex(u => u.PhoneNumber).IsUnique().HasFilter("[phone_number] IS NOT NULL")
            .HasDatabaseName("UX_Users_Phone");

        builder
            .HasMany(u => u.RefreshTokens)
            .WithOne(t => t.User)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

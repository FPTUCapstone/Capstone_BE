using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.Email).HasMaxLength(256).IsRequired();
        builder.HasIndex(u => u.Email).IsUnique();

        builder.Property(u => u.PasswordHash).IsRequired();

        builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();

        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(32);

        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(32);

        builder
            .Property(u => u.TourOperatorApplicationStatus)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder
            .HasMany(u => u.RefreshTokens)
            .WithOne(t => t.User)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

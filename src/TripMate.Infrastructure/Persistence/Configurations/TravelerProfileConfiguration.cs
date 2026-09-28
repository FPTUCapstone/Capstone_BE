using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TravelerProfileConfiguration : IEntityTypeConfiguration<TravelerProfile>
{
    public void Configure(EntityTypeBuilder<TravelerProfile> builder)
    {
        builder.ToTable("TravelerProfiles", "dbo");
        builder.HasKey(profile => profile.UserId);
        builder.Property(profile => profile.UserId).HasColumnName("user_id");
        builder.Property(profile => profile.InterestTagsJson)
            .HasColumnName("interest_tags_json")
            .HasMaxLength(1000);
        builder.Property(profile => profile.PreferredTransportMode)
            .HasColumnName("preferred_transport_mode")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false);
        builder.Property(profile => profile.TravelPace)
            .HasColumnName("travel_pace")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false);
        builder.Property(profile => profile.RiskTolerance)
            .HasColumnName("risk_tolerance")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false);
        builder.Property(profile => profile.FoodPreferencesJson)
            .HasColumnName("food_preferences_json")
            .HasMaxLength(1000);
        builder.Property(profile => profile.DefaultBudget)
            .HasColumnName("default_budget")
            .HasPrecision(12, 2);
        builder.Property(profile => profile.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .AsUtcDateTime2();
        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<TravelerProfile>(profile => profile.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

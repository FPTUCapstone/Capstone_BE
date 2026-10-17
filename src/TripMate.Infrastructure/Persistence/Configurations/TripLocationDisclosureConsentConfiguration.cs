using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TripLocationDisclosureConsentConfiguration
    : IEntityTypeConfiguration<TripLocationDisclosureConsent>
{
    public void Configure(EntityTypeBuilder<TripLocationDisclosureConsent> builder)
    {
        builder.ToTable("TripLocationDisclosureConsents", "trip");
        builder.HasKey(consent => consent.Id);
        builder.Property(consent => consent.Id).HasColumnName("consent_id").ValueGeneratedOnAdd();
        builder.Property(consent => consent.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(consent => consent.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(consent => consent.Audience).HasColumnName("audience").HasConversion<string>()
            .HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(consent => consent.GrantedAtUtc).HasColumnName("granted_at").AsUtcDateTime2().IsRequired();
        builder.Property(consent => consent.RevokedAtUtc).HasColumnName("revoked_at").AsUtcDateTime2();
        builder.HasOne(consent => consent.Session).WithMany().HasForeignKey(consent => consent.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(consent => consent.User).WithMany().HasForeignKey(consent => consent.UserId)
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(consent => new { consent.SessionId, consent.UserId, consent.Audience, consent.RevokedAtUtc })
            .HasDatabaseName("IX_TripLocationDisclosureConsents_ActiveLookup");
    }
}
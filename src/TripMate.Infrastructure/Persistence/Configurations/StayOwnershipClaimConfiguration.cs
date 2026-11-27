using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class StayOwnershipClaimConfiguration : IEntityTypeConfiguration<StayOwnershipClaim>
{
    public void Configure(EntityTypeBuilder<StayOwnershipClaim> builder)
    {
        builder.ToTable("StayOwnershipClaims", "commercial");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("claim_id");
        builder.Property(item => item.PoiId).HasColumnName("poi_id");
        builder.Property(item => item.ApplicantId).HasColumnName("applicant_id");
        builder.Property(item => item.Evidence).HasColumnName("evidence").HasMaxLength(1000);
        builder.Property(item => item.Status).HasColumnName("status").HasMaxLength(20).IsUnicode(false);
        builder.Property(item => item.SubmittedAtUtc).HasColumnName("submitted_at").AsUtcDateTime2();
        builder.Property(item => item.ReviewerId).HasColumnName("reviewer_id");
        builder.Property(item => item.ReviewNote).HasColumnName("review_note").HasMaxLength(1000);
        builder.HasIndex(item => new { item.PoiId, item.ApplicantId }).IsUnique()
            .HasFilter("[status] = 'Pending'").HasDatabaseName("UX_StayOwnershipClaims_Pending");
        builder.HasOne<PointOfInterest>().WithMany().HasForeignKey(item => item.PoiId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.ApplicantId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(item => item.ReviewerId).OnDelete(DeleteBehavior.NoAction);
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class LandingContentVersionConfiguration : IEntityTypeConfiguration<LandingContentVersion>
{
    public void Configure(EntityTypeBuilder<LandingContentVersion> builder)
    {
        builder.ToTable("LandingContentVersions", "cms");
        builder.HasKey(content => content.Id);
        builder.Property(content => content.Id).HasColumnName("content_version_id").ValueGeneratedOnAdd();
        builder.Property(content => content.VersionNumber).HasColumnName("version_number").IsRequired();
        builder.Property(content => content.IsPublished).HasColumnName("is_published").IsRequired();
        builder.Property(content => content.BaseVersionNumber).HasColumnName("base_version_number");
        builder.Property(content => content.ContentJson).HasColumnName("content_json").IsRequired();
        builder.Property(content => content.PublishedByUserId).HasColumnName("published_by_user_id");
        builder.Property(content => content.PublishedAtUtc).HasColumnName("published_at").AsUtcDateTime2();
        builder.Property(content => content.UpdatedByUserId).HasColumnName("updated_by_user_id");
        builder.Property(content => content.UpdatedAtUtc).HasColumnName("updated_at").AsUtcDateTime2().IsRequired();
        builder.Property(content => content.RowVersion).HasColumnName("row_version").IsRowVersion();

        builder.HasIndex(content => content.VersionNumber).IsUnique().HasDatabaseName("UX_LandingContentVersions_Version");
        builder.HasIndex(content => content.IsPublished).HasDatabaseName("IX_LandingContentVersions_Published");
        builder.HasIndex(content => content.IsPublished).IsUnique().HasFilter("[is_published] = 0")
            .HasDatabaseName("UX_LandingContentVersions_SingleDraft");
        builder.HasOne<User>().WithMany().HasForeignKey(content => content.PublishedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(content => content.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
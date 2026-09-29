using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags", "catalog");

        builder.HasKey(tag => tag.Id);
        builder.Property(tag => tag.Id)
            .HasColumnName("tag_id")
            .HasColumnType("int")
            .ValueGeneratedOnAdd();
        builder.Property(tag => tag.Name).HasColumnName("name").HasMaxLength(60).IsRequired();

        builder.HasIndex(tag => tag.Name).IsUnique();
    }
}
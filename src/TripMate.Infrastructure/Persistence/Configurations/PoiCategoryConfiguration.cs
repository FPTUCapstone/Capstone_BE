using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class PoiCategoryConfiguration : IEntityTypeConfiguration<PoiCategory>
{
    public void Configure(EntityTypeBuilder<PoiCategory> builder)
    {
        builder.ToTable("POICategories", "catalog");

        builder.HasKey(category => category.Id);
        builder.Property(category => category.Id)
            .HasColumnName("category_id")
            .HasColumnType("int")
            .ValueGeneratedOnAdd();

        builder.Property(category => category.Name)
            .HasColumnName("name")
            .HasMaxLength(100)
            .IsRequired();
        builder.Property(category => category.Description)
            .HasColumnName("description")
            .HasMaxLength(300);

        builder.HasIndex(category => category.Name).IsUnique();
    }
}
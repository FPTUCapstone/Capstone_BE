using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TourCategoryConfiguration : IEntityTypeConfiguration<TourCategory>
{
    public void Configure(EntityTypeBuilder<TourCategory> builder)
    {
        builder.ToTable("TourCategories", "catalog", tableBuilder =>
        {
            tableBuilder.HasCheckConstraint(
                "CK_TourCategories_CodeNotBlank",
                "LEN(LTRIM(RTRIM([code]))) > 0");
            tableBuilder.HasCheckConstraint(
                "CK_TourCategories_NameNotBlank",
                "LEN(LTRIM(RTRIM([name]))) > 0");
        });

        builder.HasKey(category => category.Id)
            .HasName("PK_TourCategories");
        builder.Property(category => category.Id)
            .HasColumnName("category_id")
            .ValueGeneratedOnAdd();
        builder.Property(category => category.Code)
            .HasColumnName("code")
            .HasMaxLength(TourCategory.CodeMaxLength)
            .IsUnicode(false)
            .UseCollation(TourCategory.EnglishCollation)
            .IsRequired();
        builder.Property(category => category.Name)
            .HasColumnName("name")
            .HasMaxLength(TourCategory.NameMaxLength)
            .UseCollation(TourCategory.EnglishCollation)
            .IsRequired();
        builder.Property(category => category.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        builder.HasIndex(category => category.Code)
            .IsUnique()
            .HasDatabaseName("UX_TourCategories_Code");
        builder.HasIndex(category => new { category.IsActive, category.Name, category.Id })
            .HasDatabaseName("IX_TourCategories_ActiveName");

        builder.Navigation(category => category.Tours)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
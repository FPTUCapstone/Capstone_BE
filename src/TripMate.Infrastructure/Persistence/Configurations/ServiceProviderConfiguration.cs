using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ServiceProviderConfiguration : IEntityTypeConfiguration<ServiceProvider>
{
    public void Configure(EntityTypeBuilder<ServiceProvider> builder)
    {
        builder.ToTable("ServiceProviders", "commercial");

        builder.HasKey(provider => provider.Id);
        builder.Property(provider => provider.Id)
            .HasColumnName("provider_id")
            .ValueGeneratedOnAdd();
        builder.Property(provider => provider.Name)
            .HasColumnName("name")
            .HasMaxLength(ServiceProvider.NameMaxLength)
            .UseCollation("Vietnamese_100_CI_AS")
            .IsRequired();
        builder.Property(provider => provider.ServiceCategory)
            .HasColumnName("service_category")
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(provider => provider.ContactEmail)
            .HasColumnName("contact_email")
            .HasMaxLength(ServiceProvider.ContactEmailMaxLength);
        builder.Property(provider => provider.ContactPhone)
            .HasColumnName("contact_phone")
            .HasMaxLength(ServiceProvider.ContactPhoneMaxLength);
        builder.Property(provider => provider.Status)
            .HasColumnName("status")
            .HasMaxLength(10)
            .IsUnicode(false)
            .HasDefaultValue(ServiceProvider.StatusActive)
            .IsRequired();

        builder.HasIndex(provider => provider.ServiceCategory)
            .HasDatabaseName("IX_ServiceProviders_Category");
    }
}
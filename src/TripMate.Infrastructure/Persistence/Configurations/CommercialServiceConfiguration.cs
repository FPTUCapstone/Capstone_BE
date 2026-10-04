using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class CommercialServiceConfiguration : IEntityTypeConfiguration<CommercialService>
{
    public void Configure(EntityTypeBuilder<CommercialService> builder)
    {
        builder.ToTable("Services", "commercial");

        builder.HasKey(service => service.Id);
        builder.Property(service => service.Id)
            .HasColumnName("service_id")
            .ValueGeneratedOnAdd();
        builder.Property(service => service.ProviderId)
            .HasColumnName("provider_id")
            .IsRequired();
        builder.Property(service => service.ServiceCategory)
            .HasColumnName("service_category")
            .HasMaxLength(20)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(service => service.Name)
            .HasColumnName("name")
            .HasMaxLength(CommercialService.NameMaxLength)
            .UseCollation("Vietnamese_100_CI_AS")
            .IsRequired();
        builder.Property(service => service.Description)
            .HasColumnName("description")
            .HasMaxLength(CommercialService.DescriptionMaxLength);
        builder.Property(service => service.PoiId).HasColumnName("poi_id");
        builder.Property(service => service.PriceAmount)
            .HasColumnName("price_amount")
            .HasPrecision(12, 2)
            .IsRequired();
        builder.Property(service => service.PriceUnit)
            .HasColumnName("price_unit")
            .HasMaxLength(12)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(service => service.Capacity).HasColumnName("capacity");
        builder.Property(service => service.AttributesJson)
            .HasColumnName("attributes_json")
            .HasMaxLength(CommercialService.AttributesJsonMaxLength);
        builder.Property(service => service.AvailabilityStatus)
            .HasColumnName("availability_status")
            .HasMaxLength(14)
            .IsUnicode(false)
            .HasDefaultValue(CommercialService.AvailabilityAvailable)
            .IsRequired();
        builder.Property(service => service.CurrencyCode)
            .HasColumnName("currency_code")
            .HasMaxLength(CommercialService.CurrencyCodeLength)
            .IsUnicode(false)
            .HasDefaultValue("VND")
            .IsRequired();
        builder.Property(service => service.PriceIncludesTax)
            .HasColumnName("price_includes_tax")
            .HasDefaultValue(false)
            .IsRequired();
        builder.Property(service => service.RefundableDepositAmount)
            .HasColumnName("refundable_deposit_amount")
            .HasPrecision(12, 2);
        builder.Property(service => service.CoverImageUrl)
            .HasColumnName("cover_image_url")
            .HasMaxLength(CommercialService.CoverImageUrlMaxLength);
        builder.Property(service => service.FulfilmentLocationLabel)
            .HasColumnName("fulfilment_location_label")
            .HasMaxLength(CommercialService.FulfilmentLocationLabelMaxLength);
        builder.Property(service => service.PickupOrArrivalInstructions)
            .HasColumnName("pickup_or_arrival_instructions")
            .HasMaxLength(CommercialService.PickupOrArrivalInstructionsMaxLength);
        builder.Property(service => service.CancellationPolicySummary)
            .HasColumnName("cancellation_policy_summary")
            .HasMaxLength(CommercialService.CancellationPolicySummaryMaxLength);
        builder.Property(service => service.LastUpdatedAtUtc)
            .HasColumnName("last_updated_at")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .AsUtcDateTime2();

        builder.HasOne(service => service.Provider)
            .WithMany()
            .HasForeignKey(service => service.ProviderId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(service => new { service.ServiceCategory, service.AvailabilityStatus })
            .HasDatabaseName("IX_Services_Category");
    }
}
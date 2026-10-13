using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class VoucherApplicableTourConfiguration : IEntityTypeConfiguration<VoucherApplicableTour>
{
    public void Configure(EntityTypeBuilder<VoucherApplicableTour> builder)
    {
        builder.ToTable("VoucherApplicableTours", "commerce");
        builder.HasKey(item => new { item.VoucherId, item.TourId });
        builder.Property(item => item.VoucherId).HasColumnName("voucher_id");
        builder.Property(item => item.TourId).HasColumnName("tour_id");
        builder.HasOne(item => item.Voucher).WithMany(nameof(Voucher.ApplicableTours)).HasForeignKey(item => item.VoucherId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Tour).WithMany().HasForeignKey(item => item.TourId).OnDelete(DeleteBehavior.Cascade);
    }
}
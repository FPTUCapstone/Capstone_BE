using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class GroupSplitBillPaymentConfiguration : IEntityTypeConfiguration<GroupSplitBillPayment>
{
    public void Configure(EntityTypeBuilder<GroupSplitBillPayment> builder)
    {
        builder.ToTable("GroupSplitBillPayments", "social");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("payment_id").ValueGeneratedOnAdd();

        builder.Property(p => p.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(p => p.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(p => p.IsPaid).HasColumnName("is_paid").IsRequired();
        builder.Property(p => p.PaidAtUtc).HasColumnName("paid_at").AsUtcDateTime2();

        builder.HasIndex(p => new { p.MessageId, p.UserId }).IsUnique();

        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class GroupMessageReadReceiptConfiguration : IEntityTypeConfiguration<GroupMessageReadReceipt>
{
    public void Configure(EntityTypeBuilder<GroupMessageReadReceipt> builder)
    {
        builder.ToTable("GroupMessageReadReceipts", "social");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("receipt_id").ValueGeneratedOnAdd();

        builder.Property(r => r.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(r => r.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(r => r.ReadAtUtc).HasColumnName("read_at").AsUtcDateTime2().IsRequired();

        builder.HasIndex(r => new { r.MessageId, r.UserId }).IsUnique();

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

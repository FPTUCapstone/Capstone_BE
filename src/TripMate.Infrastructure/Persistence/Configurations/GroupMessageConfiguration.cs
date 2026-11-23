using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class GroupMessageConfiguration : IEntityTypeConfiguration<GroupMessage>
{
    public void Configure(EntityTypeBuilder<GroupMessage> builder)
    {
        builder.ToTable("GroupMessages", "social");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("message_id").ValueGeneratedOnAdd();

        builder.Property(m => m.GroupId).HasColumnName("group_id").IsRequired();
        builder.Property(m => m.SenderUserId).HasColumnName("sender_user_id").IsRequired();
        builder.Property(m => m.MessageType).HasColumnName("message_type").HasMaxLength(20).IsRequired();
        builder.Property(m => m.Content).HasColumnName("content").IsRequired();
        builder.Property(m => m.MetadataJson).HasColumnName("metadata_json");
        builder.Property(m => m.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2().IsRequired();

        builder.HasOne(m => m.Group)
            .WithMany()
            .HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.SenderUser)
            .WithMany()
            .HasForeignKey(m => m.SenderUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(m => m.ReadReceipts)
            .WithOne(r => r.Message)
            .HasForeignKey(r => r.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(m => m.RollCallCheckins)
            .WithOne(c => c.Message)
            .HasForeignKey(c => c.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(m => m.SplitBillPayments)
            .WithOne(p => p.Message)
            .HasForeignKey(p => p.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(m => m.PollVotes)
            .WithOne(v => v.Message)
            .HasForeignKey(v => v.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(m => m.ReadReceipts).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(m => m.RollCallCheckins).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(m => m.SplitBillPayments).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(m => m.PollVotes).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

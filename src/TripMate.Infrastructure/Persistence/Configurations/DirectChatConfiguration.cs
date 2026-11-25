using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class DirectConversationConfiguration : IEntityTypeConfiguration<DirectConversation>
{
    public void Configure(EntityTypeBuilder<DirectConversation> b)
    {
        b.ToTable("DirectConversations", "social", t => t.HasCheckConstraint("CK_DirectConversations_OrderedPair", "lower_user_id < upper_user_id"));
        b.HasKey(c => c.Id);
        b.Property(c => c.Id).HasColumnName("conversation_id").ValueGeneratedOnAdd();
        b.Property(c => c.LowerUserId).HasColumnName("lower_user_id");
        b.Property(c => c.UpperUserId).HasColumnName("upper_user_id");
        b.Property(c => c.LowerReadThroughMessageId).HasColumnName("lower_read_through_message_id").IsConcurrencyToken();
        b.Property(c => c.UpperReadThroughMessageId).HasColumnName("upper_read_through_message_id").IsConcurrencyToken();
        b.Property(c => c.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        b.HasIndex(c => new { c.LowerUserId, c.UpperUserId }).IsUnique().HasDatabaseName("UX_DirectConversations_Pair");
        b.HasIndex(c => new { c.UpperUserId, c.Id }).HasDatabaseName("IX_DirectConversations_Upper");
        b.HasOne(c => c.LowerUser).WithMany().HasForeignKey(c => c.LowerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(c => c.UpperUser).WithMany().HasForeignKey(c => c.UpperUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DirectMessageConfiguration : IEntityTypeConfiguration<DirectMessage>
{
    public void Configure(EntityTypeBuilder<DirectMessage> b)
    {
        b.ToTable("DirectMessages", "social");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).HasColumnName("message_id").ValueGeneratedOnAdd();
        b.Property(m => m.ConversationId).HasColumnName("conversation_id");
        b.Property(m => m.SenderUserId).HasColumnName("sender_user_id");
        b.Property(m => m.Content).HasColumnName("content").HasMaxLength(DirectMessage.MaxContentLength).IsRequired();
        b.Property(m => m.ClientMessageId).HasColumnName("client_message_id");
        b.Property(m => m.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        b.HasIndex(m => new { m.ConversationId, m.SenderUserId, m.ClientMessageId }).IsUnique().HasDatabaseName("UX_DirectMessages_ClientKey");
        b.HasIndex(m => new { m.ConversationId, m.Id }).HasDatabaseName("IX_DirectMessages_History");
        b.HasOne(m => m.Conversation).WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.SenderUser).WithMany().HasForeignKey(m => m.SenderUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
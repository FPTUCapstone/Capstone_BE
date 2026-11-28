using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;
namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ChatGroupConfiguration : IEntityTypeConfiguration<ChatGroup>
{
    public void Configure(EntityTypeBuilder<ChatGroup> b)
    {
        b.ToTable("ChatGroups", "social");
        b.HasKey(g => g.Id);
        b.Property(g => g.Id).HasColumnName("group_id").ValueGeneratedOnAdd();
        b.Property(g => g.GroupName).HasColumnName("group_name").HasMaxLength(ChatGroup.MaxNameLength).IsRequired();
        b.Property(g => g.InitialGroupName).HasColumnName("initial_group_name").HasMaxLength(ChatGroup.MaxNameLength).IsRequired();
        b.Property(g => g.IsClosed).HasColumnName("is_closed").HasDefaultValue(false);
        b.Property(g => g.HostUserId).HasColumnName("host_user_id");
        b.Property(g => g.CreatedByUserId).HasColumnName("created_by_user_id");
        b.Property(g => g.InitialMemberUserIds).HasColumnName("initial_member_user_ids").HasMaxLength(1100).IsRequired();
        b.HasOne<User>().WithMany().HasForeignKey(g => g.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Property(g => g.ClientRequestId).HasColumnName("client_request_id");
        b.Property(g => g.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        b.HasIndex(g => new { g.CreatedByUserId, g.ClientRequestId }).IsUnique().HasDatabaseName("UX_ChatGroups_ClientKey");
        b.HasOne(g => g.HostUser).WithMany().HasForeignKey(g => g.HostUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(g => g.Members).WithOne(m => m.Group).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed class ChatGroupMemberConfiguration : IEntityTypeConfiguration<ChatGroupMember>
{
    public void Configure(EntityTypeBuilder<ChatGroupMember> b)
    {
        b.ToTable("ChatGroupMembers", "social");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).HasColumnName("member_id").ValueGeneratedOnAdd();
        b.Property(m => m.GroupId).HasColumnName("group_id");
        b.Property(m => m.UserId).HasColumnName("user_id");
        b.Property(m => m.ReadThroughMessageId).HasColumnName("read_through_message_id").IsConcurrencyToken();
        b.HasIndex(m => new { m.GroupId, m.UserId }).IsUnique().HasDatabaseName("UX_ChatGroupMembers_Membership");
        b.HasIndex(m => new { m.UserId, m.GroupId }).HasDatabaseName("IX_ChatGroupMembers_User");
        b.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class ChatGroupMessageConfiguration : IEntityTypeConfiguration<ChatGroupMessage>
{
    public void Configure(EntityTypeBuilder<ChatGroupMessage> b)
    {
        b.ToTable("ChatGroupMessages", "social");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).HasColumnName("message_id").ValueGeneratedOnAdd();
        b.Property(m => m.GroupId).HasColumnName("group_id");
        b.Property(m => m.SenderUserId).HasColumnName("sender_user_id");
        b.Property(m => m.Content).HasColumnName("content").HasMaxLength(ChatGroupMessage.MaxContentLength).IsRequired();
        b.Property(m => m.ClientMessageId).HasColumnName("client_message_id");
        b.Property(m => m.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
        b.HasIndex(m => new { m.GroupId, m.SenderUserId, m.ClientMessageId }).IsUnique().HasDatabaseName("UX_ChatGroupMessages_ClientKey");
        b.HasIndex(m => new { m.GroupId, m.Id }).HasDatabaseName("IX_ChatGroupMessages_History");
        b.HasOne(m => m.Group).WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.SenderUser).WithMany().HasForeignKey(m => m.SenderUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
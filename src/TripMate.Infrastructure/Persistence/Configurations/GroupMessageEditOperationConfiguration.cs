using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class GroupMessageEditOperationConfiguration : IEntityTypeConfiguration<GroupMessageEditOperation>
{
    public void Configure(EntityTypeBuilder<GroupMessageEditOperation> b)
    {
        b.ToTable("GroupMessageEditOperations", "social");
        b.HasKey(o => o.Id);
        b.Property(o => o.Id).HasColumnName("operation_id").ValueGeneratedOnAdd();
        b.Property(o => o.MessageId).HasColumnName("message_id");
        b.Property(o => o.ActorId).HasColumnName("actor_id");
        b.Property(o => o.ClientRequestId).HasColumnName("client_request_id");
        b.Property(o => o.ExpectedRevision).HasColumnName("expected_revision");
        b.Property(o => o.Content).HasColumnName("content").HasMaxLength(4000).IsRequired();
        b.Property(o => o.MetadataJson).HasColumnName("metadata_json");
        b.HasIndex(o => new { o.MessageId, o.ActorId, o.ClientRequestId }).IsUnique().HasDatabaseName("UX_GroupMessageEditOperations_ClientKey");
        b.HasOne(o => o.Message).WithMany().HasForeignKey(o => o.MessageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(o => o.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}
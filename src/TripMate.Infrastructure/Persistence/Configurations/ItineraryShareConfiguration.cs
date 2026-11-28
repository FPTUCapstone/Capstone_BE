using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class ItineraryShareConfiguration : IEntityTypeConfiguration<ItineraryShare>
{
    public void Configure(EntityTypeBuilder<ItineraryShare> b)
    {
        b.ToTable("ItineraryShares", "social", t => t.HasTrigger("TR_ItineraryShares_Immutable"));
        b.HasKey(s => s.ShareId);
        b.Property(s => s.ShareId).HasColumnName("share_id").ValueGeneratedNever();
        b.Property(s => s.ActorId).HasColumnName("actor_id");
        b.Property(s => s.ClientRequestId).HasColumnName("client_request_id");
        b.Property(s => s.ItineraryId).HasColumnName("itinerary_id");
        b.Property(s => s.ConversationKind).HasColumnName("conversation_kind").HasMaxLength(12).IsUnicode(false).IsRequired();
        b.Property(s => s.ConversationId).HasColumnName("conversation_id");
        b.Property(s => s.MessageId).HasColumnName("message_id");
        b.Property(s => s.Title).HasColumnName("title").HasMaxLength(Itinerary.TitleMaxLength).IsRequired();
        b.Property(s => s.SharedAtUtc).HasColumnName("shared_at").AsUtcDateTime2();
        b.Property(s => s.ItineraryVersion).HasColumnName("itinerary_version");
        b.Property(s => s.ItemsJson).HasColumnName("items_json").HasColumnType("nvarchar(max)").IsRequired();
        b.HasIndex(s => new { s.ActorId, s.ClientRequestId }).IsUnique().HasDatabaseName(ItineraryShare.RetryIndex);
        // Deliberately no itinerary/conversation cascade: snapshots survive source changes/deletion.
        // The polymorphic message reference is authorized against its real message table on every read.
        foreach (var property in b.Metadata.GetProperties())
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
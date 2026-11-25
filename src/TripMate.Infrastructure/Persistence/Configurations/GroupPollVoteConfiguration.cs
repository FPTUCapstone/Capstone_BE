using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class GroupPollVoteConfiguration : IEntityTypeConfiguration<GroupPollVote>
{
    public void Configure(EntityTypeBuilder<GroupPollVote> builder)
    {
        builder.ToTable("GroupPollVotes", "social");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("vote_id").ValueGeneratedOnAdd();

        builder.Property(v => v.MessageId).HasColumnName("message_id").IsRequired();
        builder.Property(v => v.OptionIndex).HasColumnName("option_index").IsRequired();
        builder.Property(v => v.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(v => v.VotedAtUtc).HasColumnName("voted_at").AsUtcDateTime2().IsRequired();

        builder.HasIndex(v => new { v.MessageId, v.UserId }).IsUnique();

        builder.HasOne(v => v.User)
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
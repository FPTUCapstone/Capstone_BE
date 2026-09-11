using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages", "dbo");

        builder.HasKey(m => m.MessageCode);
        builder.Property(m => m.MessageCode).HasColumnName("message_code").HasMaxLength(10).IsRequired();
        builder.Property(m => m.MessageType).HasColumnName("message_type").HasMaxLength(30).IsRequired();
        builder.Property(m => m.ContentTemplate).HasColumnName("content_template").HasMaxLength(500).IsRequired();
        builder.Property(m => m.CreatedAtUtc).HasColumnName("created_at").AsUtcDateTime2();
    }
}

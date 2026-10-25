using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets", "commerce");
        builder.HasKey(ticket => ticket.Id);
        builder.Property(ticket => ticket.Id).HasColumnName("ticket_id").ValueGeneratedOnAdd();
        builder.Property(ticket => ticket.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(ticket => ticket.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.HasIndex(ticket => ticket.BookingId).HasDatabaseName("IX_Tickets_Booking");
    }
}
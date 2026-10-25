using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence.Common;

namespace TripMate.Infrastructure.Persistence.Configurations;

public sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    /// <summary>Created by database/migrations/20261024_uc29_ticket_issuance.sql.</summary>
    public const string OneTicketPerBookingIndexName = "UX_Tickets_OneTicketPerBooking";

    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets", "commerce");
        builder.HasKey(ticket => ticket.Id);
        builder.Property(ticket => ticket.Id).HasColumnName("ticket_id").ValueGeneratedOnAdd();
        builder.Property(ticket => ticket.BookingId).HasColumnName("booking_id").IsRequired();
        builder.Property(ticket => ticket.ParticipantId).HasColumnName("participant_id");
        builder.Property(ticket => ticket.TicketCode).HasColumnName("ticket_code")
            .HasMaxLength(Ticket.TicketCodeMaxLength).IsUnicode(false).IsRequired();
        builder.Property(ticket => ticket.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(10).IsUnicode(false).IsRequired();
        builder.Property(ticket => ticket.IssuedAtUtc).HasColumnName("issued_at").AsUtcDateTime2().IsRequired();
        builder.Property(ticket => ticket.ExpiresAtUtc).HasColumnName("expires_at").AsUtcDateTime2();
        builder.Property(ticket => ticket.CheckedInAtUtc).HasColumnName("checked_in_at").AsUtcDateTime2();
        builder.HasIndex(ticket => ticket.BookingId, "IX_Tickets_Booking");
        builder.HasIndex(ticket => ticket.BookingId, OneTicketPerBookingIndexName).IsUnique();
        builder.HasIndex(ticket => ticket.TicketCode).IsUnique();
    }
}

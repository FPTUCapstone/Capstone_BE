using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Persistence.Configurations;

/// <summary>Maps commerce.BookingParticipants from database/tripmate_schema_v7.sql.</summary>
public sealed class BookingParticipantConfiguration : IEntityTypeConfiguration<BookingParticipant>
{
    public void Configure(EntityTypeBuilder<BookingParticipant> builder)
    {
        builder.ToTable("BookingParticipants", "commerce");

        builder.HasKey(participant => participant.Id);
        builder.Property(participant => participant.Id)
            .HasColumnName("participant_id")
            .ValueGeneratedOnAdd();
        builder.Property(participant => participant.BookingId)
            .HasColumnName("booking_id")
            .IsRequired();
        builder.Property(participant => participant.FullName)
            .HasColumnName("full_name")
            .HasMaxLength(BookingParticipant.FullNameMaxLength)
            .IsRequired();
        builder.Property(participant => participant.PhoneNumber)
            .HasColumnName("phone_number")
            .HasMaxLength(BookingParticipant.PhoneNumberMaxLength);
        builder.Property(participant => participant.Email)
            .HasColumnName("email")
            .HasMaxLength(BookingParticipant.EmailMaxLength);
        builder.Property(participant => participant.ParticipantType)
            .HasColumnName("participant_type")
            .HasConversion<string>()
            .HasMaxLength(10)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(participant => participant.IdDocumentType)
            .HasColumnName("id_document_type")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false);
        builder.Property(participant => participant.IdDocumentNumber)
            .HasColumnName("id_document_number")
            .HasMaxLength(BookingParticipant.IdDocumentNumberCiphertextMaxLength);
        builder.Property(participant => participant.Nationality)
            .HasColumnName("nationality")
            .HasMaxLength(BookingParticipant.NationalityLength)
            .IsFixedLength();

        builder.HasIndex(participant => participant.BookingId)
            .HasDatabaseName("IX_BookingParticipants_Booking");
    }
}
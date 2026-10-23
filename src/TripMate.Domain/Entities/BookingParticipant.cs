using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Named ticket-holder for a booking, stored in commerce.BookingParticipants.
/// id_document_number is PII (BR-33) and must be encrypted before persistence by the
/// application layer; this entity holds whatever ciphertext/plaintext it is given.
/// </summary>
public class BookingParticipant : BaseEntity
{
    public const int FullNameMaxLength = 150;
    public const int PhoneNumberMaxLength = 20;
    public const int EmailMaxLength = 256;
    /// <summary>Maximum clear-text document number accepted from the API.</summary>
    public const int IdDocumentNumberMaxLength = 50;

    /// <summary>
    /// Maximum persisted Data Protection ciphertext length. This is deliberately distinct from
    /// <see cref="IdDocumentNumberMaxLength"/> because protected values are substantially longer
    /// than their clear-text input.
    /// </summary>
    public const int IdDocumentNumberCiphertextMaxLength = 512;
    public const int NationalityLength = 3;

    private BookingParticipant()
    {
    }

    public long BookingId { get; private set; }

    public Booking Booking { get; private set; } = null!;

    public string FullName { get; private set; } = string.Empty;

    public ParticipantType ParticipantType { get; private set; }

    public string? PhoneNumber { get; private set; }

    public string? Email { get; private set; }

    public BookingIdDocumentType? IdDocumentType { get; private set; }

    public string? IdDocumentNumber { get; private set; }

    public string? Nationality { get; private set; }

    public static BookingParticipant Create(string fullName, ParticipantType participantType,
        string? phoneNumber, string? email, BookingIdDocumentType? idDocumentType,
        string? idDocumentNumber, string? nationality)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        var normalizedName = fullName.Trim();
        if (normalizedName.Length > FullNameMaxLength)
            throw new ArgumentOutOfRangeException(nameof(fullName));
        if (!Enum.IsDefined(participantType))
            throw new ArgumentOutOfRangeException(nameof(participantType));

        var normalizedPhone = string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
        if (normalizedPhone?.Length > PhoneNumberMaxLength)
            throw new ArgumentOutOfRangeException(nameof(phoneNumber));

        var normalizedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (normalizedEmail?.Length > EmailMaxLength)
            throw new ArgumentOutOfRangeException(nameof(email));

        var normalizedDocumentNumber = string.IsNullOrWhiteSpace(idDocumentNumber) ? null : idDocumentNumber.Trim();
        if (normalizedDocumentNumber?.Length > IdDocumentNumberCiphertextMaxLength)
            throw new ArgumentOutOfRangeException(nameof(idDocumentNumber));

        var normalizedNationality = string.IsNullOrWhiteSpace(nationality) ? null : nationality.Trim().ToUpperInvariant();
        if (normalizedNationality is not null && normalizedNationality.Length != NationalityLength)
            throw new ArgumentOutOfRangeException(nameof(nationality));

        return new BookingParticipant
        {
            FullName = normalizedName,
            ParticipantType = participantType,
            PhoneNumber = normalizedPhone,
            Email = normalizedEmail,
            IdDocumentType = idDocumentType,
            IdDocumentNumber = normalizedDocumentNumber,
            Nationality = normalizedNationality,
        };
    }

    internal void AttachTo(Booking booking)
    {
        Booking = booking ?? throw new ArgumentNullException(nameof(booking));
    }
}
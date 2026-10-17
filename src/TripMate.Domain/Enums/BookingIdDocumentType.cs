namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the commerce.BookingParticipants.id_document_type SQL check constraint.
/// Named distinctly from OperatorDocumentType, which covers a different table/domain.
/// </summary>
public enum BookingIdDocumentType
{
    CCCD = 1,
    Passport = 2,
    Other = 3,
}

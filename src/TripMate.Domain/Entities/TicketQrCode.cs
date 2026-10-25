using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Signed, encrypted QR payload of a <see cref="Ticket"/>, stored in commerce.TicketQrCodes.
/// At most one row per ticket is Active (UX_TicketQrCodes_OneActivePerTicket); a refresh revokes
/// it and issues a new one. Consumed is reserved for the UC-43 check-in.
/// </summary>
public sealed class TicketQrCode : BaseEntity
{
    public const int QrTokenMaxLength = 500;
    public const int SignatureMaxLength = 500;

    private TicketQrCode()
    {
    }

    public long TicketId { get; private set; }

    public string QrToken { get; private set; } = string.Empty;

    public string Signature { get; private set; } = string.Empty;

    public TicketQrCodeStatus Status { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public static TicketQrCode Issue(long ticketId, string qrToken, string signature, DateTimeOffset issuedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticketId);
        ArgumentException.ThrowIfNullOrWhiteSpace(qrToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        if (qrToken.Length > QrTokenMaxLength)
            throw new ArgumentOutOfRangeException(nameof(qrToken));
        if (signature.Length > SignatureMaxLength)
            throw new ArgumentOutOfRangeException(nameof(signature));

        return new TicketQrCode
        {
            TicketId = ticketId,
            QrToken = qrToken,
            Signature = signature,
            Status = TicketQrCodeStatus.Active,
            IssuedAtUtc = issuedAtUtc.ToUniversalTime(),
        };
    }

    /// <summary>Returns false when the code is no longer Active, so repeating it is a no-op.</summary>
    public bool Revoke()
    {
        if (Status != TicketQrCodeStatus.Active)
            return false;

        Status = TicketQrCodeStatus.Revoked;
        return true;
    }
}

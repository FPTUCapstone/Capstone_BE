using TripMate.Application.Common.Models;

namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// UC-29 QR payload (BR-83, PC-02): an encrypted, time-stamped and signed token that only the server
/// can produce or read. Clients render <see cref="TicketQrPayload.Content"/> as-is.
/// </summary>
public interface ITicketQrCodec
{
    TicketQrPayload Issue(long ticketId, DateTimeOffset issuedAtUtc);

    /// <summary>Rebuilds the QR content from a stored token and signature.</summary>
    string ContentFor(string qrToken, string signature);

    /// <summary>For the UC-43 scanner. Returns false, never throws, for any forged or malformed content.</summary>
    bool TryVerify(string content, out TicketQrVerification? verification);
}

using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Append-only scan audit trail stored in commerce.CheckInLogs (UC-43).
/// Records successful check-ins and already-used repeat scans.
/// </summary>
public sealed class CheckInLog : BaseEntity
{
    private CheckInLog()
    {
    }

    public long TicketId { get; private set; }

    public Ticket Ticket { get; private set; } = null!;

    public long? QrCodeId { get; private set; }

    public TicketQrCode? QrCode { get; private set; }

    public long? CheckedInBy { get; private set; }

    public User? CheckedInByUser { get; private set; }

    public CheckInScanResult ScanResult { get; private set; }

    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    public DateTimeOffset CheckedInAtUtc { get; private set; }

    public static CheckInLog Create(
        long ticketId,
        long? qrCodeId,
        long? checkedInBy,
        CheckInScanResult scanResult,
        DateTimeOffset checkedInAtUtc,
        decimal? latitude = null,
        decimal? longitude = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticketId);

        return new CheckInLog
        {
            TicketId = ticketId,
            QrCodeId = qrCodeId,
            CheckedInBy = checkedInBy,
            ScanResult = scanResult,
            CheckedInAtUtc = checkedInAtUtc.ToUniversalTime(),
            Latitude = latitude,
            Longitude = longitude,
        };
    }
}

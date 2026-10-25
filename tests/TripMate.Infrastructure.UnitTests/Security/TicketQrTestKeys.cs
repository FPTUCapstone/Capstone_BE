namespace TripMate.Infrastructure.UnitTests.Security;

/// <summary>
/// Test-only UC-29 QR keys (bytes 1..32 and 33..64) for tests that build the whole infrastructure
/// graph, which refuses to start without usable TicketQr keys (plan A2).
/// </summary>
internal static class TicketQrTestKeys
{
    public const string EncryptionKeySetting = "TicketQr:EncryptionKey";
    public const string SigningKeySetting = "TicketQr:SigningKey";
    public const string EncryptionKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
    public const string SigningKey = "ISIjJCUmJygpKissLS4vMDEyMzQ1Njc4OTo7PD0+P0A=";
}

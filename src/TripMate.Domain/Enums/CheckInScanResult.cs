namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the commerce.CheckInLogs.scan_result SQL check constraint:
/// ('Success', 'AlreadyUsed', 'Invalid', 'Expired').
/// </summary>
public enum CheckInScanResult
{
    Success,
    AlreadyUsed,
    Invalid,
    Expired,
}
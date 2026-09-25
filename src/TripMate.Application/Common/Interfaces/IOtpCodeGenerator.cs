namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Generates the 6-digit reset OTP string (leading zeros valid). Raw codes are transient:
/// never logged, never persisted — only the protected representation is stored.
/// </summary>
public interface IOtpCodeGenerator
{
    string Generate();
}
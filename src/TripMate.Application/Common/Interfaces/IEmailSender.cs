using TripMate.Application.Common.Models;

namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Application email port for the password-reset flow. The raw OTP is passed transiently
/// for message composition and transport only: implementations must never persist, cache,
/// or log it. Implementations classify every outcome as Delivered, DefiniteFailure, or
/// Unknown without exposing SMTP, MailKit, or provider exception details to Application.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends the password-reset OTP email to <paramref name="destinationEmail"/>.</summary>
    Task<EmailDeliveryResult> SendPasswordResetOtpAsync(
        string destinationEmail,
        string otp,
        CancellationToken cancellationToken);
}
using TripMate.Application.Common.Models;

namespace TripMate.Infrastructure.Email;

/// <summary>
/// Builds the password-reset email content. The validity period is derived from the
/// authoritative <see cref="PasswordResetPolicy.OtpTimeToLive"/> — never hard-coded here.
/// The raw OTP exists only transiently in the returned content; nothing is logged.
/// </summary>
internal static class PasswordResetEmailComposer
{
    public static PasswordResetEmailContent Compose(string otp)
    {
        var validityMinutes = (int)PasswordResetPolicy.OtpTimeToLive.TotalMinutes;

        return new PasswordResetEmailContent(
            Subject: "Your TripMate password reset code",
            TextBody: $"""
                You (or someone else) requested a password reset for your TripMate account.

                Your verification code is: {otp}

                The code is valid for {validityMinutes} minutes. If you did not request a password reset, you can safely ignore this email.
                """);
    }
}

internal sealed record PasswordResetEmailContent(string Subject, string TextBody);
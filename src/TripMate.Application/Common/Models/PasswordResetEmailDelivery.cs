namespace TripMate.Application.Common.Models;

/// <summary>
/// Process-local, transient email work item. <see cref="Otp"/> is sensitive and must never
/// be persisted or logged. String rendering deliberately excludes the email address and OTP.
/// </summary>
public sealed class PasswordResetEmailDelivery
{
    public PasswordResetEmailDelivery(long userId, long generation, string destinationEmail, string otp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(otp);

        UserId = userId;
        Generation = generation;
        DestinationEmail = destinationEmail;
        Otp = otp;
    }

    public long UserId { get; }

    public long Generation { get; }

    public string DestinationEmail { get; }

    public string Otp { get; }

    public override string ToString() =>
        $"{nameof(PasswordResetEmailDelivery)} {{ UserId = {UserId}, Generation = {Generation} }}";
}
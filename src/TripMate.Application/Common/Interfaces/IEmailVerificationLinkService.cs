namespace TripMate.Application.Common.Interfaces;

using TripMate.Application.Common.Models;

public interface IEmailVerificationLinkService
{
    Task<string> GenerateAsync(string email, CancellationToken cancellationToken);
}

public interface IEmailVerificationStatusService
{
    Task<bool> IsVerifiedAsync(string email, CancellationToken cancellationToken);
}

public interface IEmailVerificationSender
{
    Task<EmailDeliveryResult> SendAsync(
        string destinationEmail,
        string verificationLink,
        CancellationToken cancellationToken);
}

public interface IEmailVerificationResendCooldown
{
    bool TryAcquire(long userId, DateTimeOffset now, TimeSpan cooldown);
}

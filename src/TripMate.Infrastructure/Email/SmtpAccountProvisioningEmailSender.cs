using TripMate.Application.Common.Models;
using TripMate.Application.Features.AccountProvisioning;

namespace TripMate.Infrastructure.Email;

public sealed class SmtpAccountProvisioningEmailSender(SmtpEmailSender sender) : IAccountProvisioningEmailSender
{
    public Task<EmailDeliveryResult> SendTemporaryPasswordAsync(string email, string temporaryPassword,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPassword);
        return sender.SendAsync(email, new PasswordResetEmailContent(
            "Your TripMate account",
            $"Your TripMate account has been created.\n\nYour temporary password is {temporaryPassword}\n\nChange this password at your first sign-in."),
            "Account provisioning", cancellationToken);
    }
}
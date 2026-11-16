using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.AccountProvisioning;

public interface IAccountProvisioningEmailSender
{
    Task<EmailDeliveryResult> SendTemporaryPasswordAsync(string email, string temporaryPassword,
        CancellationToken cancellationToken);
}
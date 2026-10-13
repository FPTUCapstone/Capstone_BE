using TripMate.Application.Common.Models;

namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Delivers non-sensitive administrative account notifications after their originating
/// transaction has committed. Implementations return a classified outcome and never expose
/// SMTP/provider details to callers.
/// </summary>
public interface IUserAdministrationNotificationEmailSender
{
    Task<EmailDeliveryResult> SendAsync(
        string destinationEmail,
        string subject,
        string body,
        CancellationToken cancellationToken);
}
using FirebaseAdmin;
using FirebaseAdmin.Messaging;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.TripAlerts;

internal sealed class FirebaseTripAlertPushSender(
    IPushTokenProtector protector,
    IFirebaseAuthService firebaseAuthService)
{
    public async Task<bool> SendAsync(
        ClaimedTripAlert alert,
        CancellationToken cancellationToken)
    {
        _ = firebaseAuthService; // Resolving it initializes the shared FirebaseApp.
        var tokens = alert.ProtectedDeviceTokens
            .Select(protector.Unprotect)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tokens.Length == 0 || FirebaseApp.DefaultInstance is null)
        {
            return false;
        }

        var message = new MulticastMessage
        {
            Tokens = tokens,
            Notification = new FirebaseAdmin.Messaging.Notification
            {
                Title = alert.Title,
                Body = alert.Body,
            },
            Data = new Dictionary<string, string>
            {
                ["notificationId"] = alert.NotificationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["incidentId"] = alert.IncidentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sessionId"] = alert.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["type"] = "TripAlert",
            },
        };
        BatchResponse response = await FirebaseMessaging.DefaultInstance
            .SendEachForMulticastAsync(message, cancellationToken);
        return response.SuccessCount > 0;
    }
}
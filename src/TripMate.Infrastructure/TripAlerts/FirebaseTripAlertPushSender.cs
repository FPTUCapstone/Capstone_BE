using FirebaseAdmin;
using FirebaseAdmin.Messaging;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.TripAlerts;

internal sealed record TripAlertPushAttempt(
    string ProtectedToken,
    bool IsSuccess,
    MessagingErrorCode? ErrorCode);

internal sealed record TripAlertPushResult(
    bool Sent,
    IReadOnlyCollection<string> InvalidProtectedTokens)
{
    public static TripAlertPushResult Classify(
        IReadOnlyCollection<TripAlertPushAttempt> attempts) =>
        new(
            attempts.Any(attempt => attempt.IsSuccess),
            attempts
                .Where(attempt => !attempt.IsSuccess
                    && attempt.ErrorCode is MessagingErrorCode.Unregistered
                        or MessagingErrorCode.SenderIdMismatch)
                .Select(attempt => attempt.ProtectedToken)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
}

internal sealed class FirebaseTripAlertPushSender(
    IPushTokenProtector protector,
    IFirebaseAuthService firebaseAuthService)
{
    public async Task<TripAlertPushResult> SendAsync(
        ClaimedTripAlert alert,
        CancellationToken cancellationToken)
    {
        _ = firebaseAuthService; // Resolving it initializes the shared FirebaseApp.
        var devices = alert.ProtectedDeviceTokens
            .Select(protectedToken => new
            {
                ProtectedToken = protectedToken,
                Token = protector.Unprotect(protectedToken),
            })
            .DistinctBy(device => device.Token, StringComparer.Ordinal)
            .ToArray();
        if (devices.Length == 0 || FirebaseApp.DefaultInstance is null)
        {
            return new TripAlertPushResult(false, []);
        }

        var message = new MulticastMessage
        {
            Tokens = devices.Select(device => device.Token).ToArray(),
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
        return TripAlertPushResult.Classify(response.Responses
            .Select((result, index) => new TripAlertPushAttempt(
                devices[index].ProtectedToken,
                result.IsSuccess,
                (result.Exception as FirebaseMessagingException)?.MessagingErrorCode))
            .ToArray());
    }
}
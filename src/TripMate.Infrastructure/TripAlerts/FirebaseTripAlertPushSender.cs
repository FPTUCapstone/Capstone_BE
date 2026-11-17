using FirebaseAdmin;
using FirebaseAdmin.Messaging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripAlerts.Common;

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

        var message = BuildMessage(devices.Select(device => device.Token).ToArray(), alert);
        BatchResponse response = await FirebaseMessaging.DefaultInstance
            .SendEachForMulticastAsync(message, cancellationToken);
        return TripAlertPushResult.Classify(response.Responses
            .Select((result, index) => new TripAlertPushAttempt(
                devices[index].ProtectedToken,
                result.IsSuccess,
                (result.Exception as FirebaseMessagingException)?.MessagingErrorCode))
            .ToArray());
    }

    internal static MulticastMessage BuildMessage(
        IReadOnlyList<string> tokens,
        ClaimedTripAlert alert) => new()
        {
            Tokens = tokens,
            Notification = new FirebaseAdmin.Messaging.Notification
            {
                Title = alert.Title,
                Body = alert.Body,
            },
            Data = alert.Data,
            // Wakes a background or killed app promptly; the content stays generic.
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = alert.Data.GetValueOrDefault("type") switch
                {
                    EmergencyPushContract.ReviewType => TrayEntry(EmergencyPushContract.ReviewTrayTag),
                    EmergencyPushContract.BookingType => TrayEntry(EmergencyPushContract.BookingTrayTag),
                    _ => null,
                },
            },
        };

    // The timestamp must be set: left out, the SDK sends year 0001 and the tray misreports the age.
    private static AndroidNotification TrayEntry(string tag) =>
        new() { Tag = tag, EventTimestamp = DateTime.UtcNow };
}
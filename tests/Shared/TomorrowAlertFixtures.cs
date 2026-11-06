using System.Security.Cryptography;
using System.Text;

namespace TripMate.Tests.Shared;

/// <summary>
/// Tomorrow.io Alerts payloads with the structure published at
/// https://docs.tomorrow.io/reference/overview-alerts ("Custom Insight Payload") and the
/// notification list item at https://docs.tomorrow.io/reference/list-notifications-get.
/// The documented placeholder <c>...alert</c> is filled with the alert object returned by
/// https://docs.tomorrow.io/reference/post-alerts. Tests swap only the identifiers and times.
/// </summary>
public static class TomorrowAlertFixtures
{
    public const string DocsAlertId = "38f689d83c264eb0b084ba095f2ea743";
    public const string DocsInsightId = "38f689d83c264eb0b084ba095f2ea332";
    public const string DocsLocationId = "38f689d83c264eb0b084ba39975f3413";

    // Verbatim from the docs apart from the trailing commas the docs sample carries.
    private const string CustomInsightWebhook = """
        {
            "data": {
                "alert": {
                    "id": "38f689d83c264eb0b084ba095f2ea743",
                    "insight": "38f689d83c264eb0b084ba095f2ea332",
                    "name": "De-icing Conditions Alert",
                    "isActive": true,
                    "notifications": [{"type": "PRIOR", "value": 30}, {"type": "START"}, {"type": "END", "value": 120}],
                    "createdAt": "2020-05-10T06:49:34+0000"
                },
                "insight": {
                    "id": "38f689d83c264eb0b084ba095f2ea332"
                },
                "locations": [
                  { "id": "38f689d83c264eb0b084ba39975f3413" }
                ],
                "notification": {
                    "type": "PRIOR",
                    "value": 30,
                    "title": "Low Visibility Advisory",
                    "description": "* WHAT... Visibility 1km or less * WHEN... in 30 minutes * IMPACTS... Hazardous driving conditions due to low visibility * ACTIONS... If driving, slow down, use your headlights, and leave plenty of distance ahead of you."
                },
                "event": {
                    "startTime": "2020-05-10T07:49:34+0000",
                    "eventValues": {
                        "visibility": 0.52
                    },
                    "triggerValues": {
                        "visibility": 0.52
                    }
                }
            }
        }
        """;

    private const string CustomInsightEndWebhook = """
        {
            "data": {
                "alert": {
                    "id": "38f689d83c264eb0b084ba095f2ea743",
                    "insight": "38f689d83c264eb0b084ba095f2ea332",
                    "name": "De-icing Conditions Alert",
                    "isActive": true,
                    "createdAt": "2020-05-10T06:49:34+0000"
                },
                "insight": {
                    "id": "38f689d83c264eb0b084ba095f2ea332"
                },
                "locations": [
                  { "id": "38f689d83c264eb0b084ba39975f3413" }
                ],
                "notification": {
                    "type": "END",
                    "value": 120
                },
                "event": {
                    "endTime": "2020-05-10T07:49:34+0000",
                    "eventValues": {
                        "visibility": 3.1
                    },
                    "triggerValues": {
                        "visibility": 3.1
                    }
                }
            }
        }
        """;

    // One item of data.notifications in GET /v4/notifications (docs "Result" example).
    private const string NotificationListItem = """
        {
            "id": "61d2e5719550aa00085ac502",
            "ownerId": "60939e9443c50ec7087a9142",
            "title": "Wind Alert - Keep wind catching materials low and flat to avoid catching wind has started",
            "description": "",
            "status": "sent",
            "accountId": "5b168c92a205ab57e15eb52",
            "createdAt": "2022-01-03T12:00:49.930Z",
            "isRead": false,
            "updatedAt": "2022-01-03T12:00:49.930Z",
            "type": "START",
            "alert": {
                "type": "alert",
                "name": "Fire",
                "id": "38f689d83c264eb0b084ba095f2ea743",
                "creatorId": "60939e9443c254005a5e9142",
                "event": {
                    "expectedTime": "2020-05-10T07:49:34+0000",
                    "triggeredConditions": [
                        { "name": "windSpeedMax", "value": "5.88", "units": "m/s" }
                    ]
                },
                "insight": {
                    "id": "38f689d83c264eb0b084ba095f2ea332",
                    "name": "Wind Alert - Keep wind catching materials low and flat to avoid catching wind",
                    "severity": "minor"
                }
            },
            "locations": [
                {
                    "id": "38f689d83c264eb0b084ba39975f3413",
                    "name": "Copacabana, Rio de Janeiro - State of Rio de Janeiro, Brazil",
                    "geometryType": "Point"
                }
            ]
        }
        """;

    public static string Webhook(
        string type = "PRIOR",
        string alertId = DocsAlertId,
        string locationId = DocsLocationId,
        DateTimeOffset? startTime = null) =>
        Swap(CustomInsightWebhook, alertId, locationId, startTime)
            .Replace("\"type\": \"PRIOR\",\n            \"value\": 30,", Notification(type), StringComparison.Ordinal);

    public static string EndWebhook(
        string alertId = DocsAlertId,
        string locationId = DocsLocationId,
        DateTimeOffset? endTime = null) =>
        Swap(CustomInsightEndWebhook, alertId, locationId, endTime);

    /// <summary>The webhook without optional fields: no title, description, value or values.</summary>
    public static string MinimalWebhook(
        string type,
        string alertId = DocsAlertId,
        string locationId = DocsLocationId,
        DateTimeOffset? startTime = null) =>
        "{\"data\":{\"alert\":{\"id\":\"" + alertId + "\"},\"insight\":{\"id\":\"" + DocsInsightId
        + "\"},\"locations\":[{\"id\":\"" + locationId + "\"}],\"notification\":{\"type\":\"" + type
        + "\"},\"event\":{\"startTime\":\"" + Format(startTime) + "\"}}}";

    public static string ListItem(
        string notificationId = "61d2e5719550aa00085ac502",
        string alertId = DocsAlertId,
        string locationId = DocsLocationId,
        DateTimeOffset? expectedTime = null) =>
        Swap(NotificationListItem, alertId, locationId, expectedTime)
            .Replace("61d2e5719550aa00085ac502", notificationId, StringComparison.Ordinal);

    /// <summary>GET /v4/notifications body as documented: data.notifications plus links.</summary>
    public static string ListResponse(string? next, params string[] items)
    {
        string nextLink = next is null ? string.Empty : $",\"next\":\"{next}\"";
        return "{\"data\":{\"notifications\":[" + string.Join(',', items)
            + "]},\"links\":{\"query\":{},\"self\":\"/v4/notifications?limit=100&offset=0\"" + nextLink + "}}";
    }

    /// <summary>The documented signing algorithm: HMAC-SHA256 over the timestamp string only.</summary>
    public static string SignatureHeader(string secret, DateTimeOffset signedAt)
    {
        string timestamp = signedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        string signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp))).ToLowerInvariant();
        return $"t={timestamp},sig={signature}";
    }

    private static string Notification(string type) => type switch
    {
        "PRIOR" => "\"type\": \"PRIOR\",\n            \"value\": 30,",
        _ => $"\"type\": \"{type}\",",
    };

    private static string Swap(string json, string alertId, string locationId, DateTimeOffset? time) =>
        json.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace(DocsAlertId, alertId, StringComparison.Ordinal)
            .Replace(DocsLocationId, locationId, StringComparison.Ordinal)
            .Replace("2020-05-10T07:49:34+0000", Format(time), StringComparison.Ordinal);

    // The docs write offsets as +0000; keep that shape so parsing is exercised as sent.
    private static string Format(DateTimeOffset? time) =>
        (time ?? new DateTimeOffset(2020, 5, 10, 7, 49, 34, TimeSpan.Zero))
            .ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+0000'", System.Globalization.CultureInfo.InvariantCulture);
}
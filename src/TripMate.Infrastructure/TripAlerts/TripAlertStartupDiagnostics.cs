namespace TripMate.Infrastructure.TripAlerts;

/// <summary>
/// Misconfiguration that silently turns parts of UC-14 off. Logged once at startup so an operator
/// can see it without reading code.
/// </summary>
internal static class TripAlertStartupDiagnostics
{
    public static IReadOnlyList<string> Warnings(bool weatherEnabled, bool pushReady)
    {
        var warnings = new List<string>();
        if (!weatherEnabled)
        {
            warnings.Add("UC-14 weather alerts are off: set Tomorrow__Enabled=true and Tomorrow__ApiKey. "
                + "Rain and thunderstorm alerts are not created until then.");
        }

        if (!pushReady)
        {
            warnings.Add("UC-14 push is unavailable: Firebase Admin is not initialized. Provide "
                + "GOOGLE_APPLICATION_CREDENTIALS or Firebase__ServiceAccountKeyJson. The in-app Alerts list still works.");
        }

        return warnings;
    }
}
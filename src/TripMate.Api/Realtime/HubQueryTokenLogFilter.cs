using Serilog.Core;
using Serilog.Events;

namespace TripMate.Api.Realtime;

/// <summary>
/// Browsers can only send the hub JWT in the URL, which request logs would repeat.
/// </summary>
public sealed class HubQueryTokenLogFilter : ILogEventFilter
{
    public bool IsEnabled(LogEvent logEvent) =>
        !logEvent.Properties.TryGetValue("QueryString", out var query)
        || !query.ToString().Contains("access_token=", StringComparison.OrdinalIgnoreCase);
}
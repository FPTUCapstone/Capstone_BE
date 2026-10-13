using System.Diagnostics.Metrics;

namespace TripMate.Application.Features.Navigation.Common;

internal static class NavigationSessionMetrics
{
    public const string MeterName = "TripMate.Navigation";
    public const string StartsName = "tripmate.navigation.starts";
    public const string ReachesName = "tripmate.navigation.reaches";
    public const string CompletionsName = "tripmate.navigation.completions";
    public const string SkipsName = "tripmate.navigation.skips";
    public const string DeparturesName = "tripmate.navigation.departures";
    public const string ExpirationsName = "tripmate.navigation.expirations";
    public const string AcceptedOutcome = "accepted";
    public const string ReplayedOutcome = "replayed";
    public const string RejectedOutcome = "rejected";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> Starts = Meter.CreateCounter<long>(StartsName);
    private static readonly Counter<long> Reaches = Meter.CreateCounter<long>(ReachesName);
    private static readonly Counter<long> Completions = Meter.CreateCounter<long>(CompletionsName);
    private static readonly Counter<long> Skips = Meter.CreateCounter<long>(SkipsName);
    private static readonly Counter<long> Departures = Meter.CreateCounter<long>(DeparturesName);
    private static readonly Counter<long> Expirations = Meter.CreateCounter<long>(ExpirationsName);

    public static void RecordStart(string outcome) => Record(Starts, outcome);

    public static void RecordReach(string outcome) => Record(Reaches, outcome);

    public static void RecordCompletion(string outcome) => Record(Completions, outcome);

    public static void RecordSkip(string outcome) => Record(Skips, outcome);

    public static void RecordDeparture(string outcome) => Record(Departures, outcome);

    public static void RecordExpiration(string outcome) => Record(Expirations, outcome);

    private static void Record(Counter<long> counter, string outcome)
    {
        try
        {
            counter.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // Observability is best-effort and must never alter navigation behavior.
        }
    }
}
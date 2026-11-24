namespace TripMate.Application.Features.Rerouting.Common;

/// <summary>
/// UC-15 configuration (section <c>Rerouting</c>). The search radius is not here: it belongs to UC-57
/// and lives in <c>dbo.SystemConfigs</c> under <c>Rerouting.SearchRadiusKm</c>.
/// </summary>
public sealed class ReroutingOptions
{
    public const string SectionName = "Rerouting";

    public static readonly TimeSpan MinProposalTtl = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxProposalTtl = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan MinRetryWindow = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxRetryWindow = TimeSpan.FromMinutes(240);
    public const int MinMaxVariants = 1;
    public const int MaxMaxVariants = 20;
    public const int MinMaxReplacementsPerStop = 1;
    public const int MaxMaxReplacementsPerStop = 10;

    /// <summary>A proposal is open while <c>now &lt;= proposed_at + ProposalTtl</c> (SRS 6.a1).</summary>
    public TimeSpan ProposalTtl { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Upper bound of variants evaluated for one incident.</summary>
    public int MaxVariants { get; init; } = 8;

    /// <summary>Replacement candidates tried for one affected stop.</summary>
    public int MaxReplacementsPerStop { get; init; } = 3;

    /// <summary>
    /// How long after detection an incident that still has no proposal is retried by the scan, so an
    /// infeasible incident is not re-evaluated every scan interval forever.
    /// </summary>
    public TimeSpan ProposalRetryWindow { get; init; } = TimeSpan.FromMinutes(30);
}
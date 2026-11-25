namespace TripMate.Application.Features.Rerouting.Common;

public static class ReroutingErrorCodes
{
    /// <summary>Unknown session or proposal, a proposal of another session, another Traveler's session, or a non-v1 snapshot (SRS MSG44).</summary>
    public const string NotFound = "rerouting.not_found";

    /// <summary>TTL passed, the session ended, or the base stops changed (SRS MSG44).</summary>
    public const string ProposalExpired = "rerouting.proposal_expired";

    /// <summary>Already closed the other way, for example an accept after a reject (SRS MSG44).</summary>
    public const string ProposalClosed = "rerouting.proposal_closed";

    /// <summary>The fresh evaluation at accept time is no longer feasible (SRS MSG44).</summary>
    public const string ProposalInfeasible = "rerouting.proposal_infeasible";
}
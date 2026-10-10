using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class TripSession : BaseEntity
{
    public const string PlanningState = "Planning";
    public const string NavigatingState = "Navigating";
    public const string ExploringState = "Exploring";
    public const string InterruptedState = "Interrupted";
    public const string CompletedState = "Completed";
    public const string RouteFinishedReason = "RouteFinished";
    public const string TravelerStoppedReason = "TravelerStopped";
    public const string ExpiredReason = "Expired";

    private readonly List<TripSessionItem> _items = [];
    private readonly List<TripStateHistory> _stateHistory = [];

    private TripSession()
    {
    }

    public long ItineraryId { get; private set; }
    public Itinerary Itinerary { get; private set; } = null!;
    public long RequestedItineraryId { get; private set; }
    public long TravelerUserId { get; private set; }
    public User TravelerUser { get; private set; } = null!;
    public Guid StartIdempotencyKey { get; private set; }
    public string FsmState { get; private set; } = PlanningState;
    public string? CompletionReason { get; private set; }
    public long? ExploringItemId { get; private set; }
    public decimal? CurrentLatitude { get; private set; }
    public decimal? CurrentLongitude { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? ExpiresAtUtc { get; private set; }
    public DateTimeOffset? EndedAtUtc { get; private set; }
    public DateTimeOffset? LastSyncedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyCollection<TripSessionItem> Items => _items.AsReadOnly();
    public IReadOnlyCollection<TripStateHistory> StateHistory => _stateHistory.AsReadOnly();

    private bool IsCompleted => FsmState == CompletedState;

    private bool HasPendingItems => _items.Any(item => item.Status == TripSessionItem.PendingStatus);

    public static TripSession Start(
        long itineraryId,
        long requestedItineraryId,
        long travelerUserId,
        Guid startIdempotencyKey,
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc,
        IEnumerable<TripSessionItem> items)
    {
        if (itineraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itineraryId));
        }

        if (requestedItineraryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedItineraryId));
        }

        if (travelerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        }

        if (startIdempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(startIdempotencyKey));
        }

        EnsureUtc(startedAtUtc, nameof(startedAtUtc));
        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (expiresAtUtc <= startedAtUtc)
        {
            throw new ArgumentException("The session must expire after it starts.", nameof(expiresAtUtc));
        }

        ArgumentNullException.ThrowIfNull(items);
        var orderedItems = items.OrderBy(item => item.SequenceNo).ToArray();
        if (orderedItems.Length == 0)
        {
            throw new ArgumentException("At least one navigable item is required.", nameof(items));
        }

        if (orderedItems.Select(item => item.SequenceNo).Distinct().Count() != orderedItems.Length
            || orderedItems.Select(item => item.ItineraryItemId).Distinct().Count() != orderedItems.Length)
        {
            throw new ArgumentException("Navigation item identity and sequence must be unique.", nameof(items));
        }

        var session = new TripSession
        {
            ItineraryId = itineraryId,
            RequestedItineraryId = requestedItineraryId,
            TravelerUserId = travelerUserId,
            StartIdempotencyKey = startIdempotencyKey,
            FsmState = NavigatingState,
            StartedAtUtc = startedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
        };

        foreach (var item in orderedItems)
        {
            item.AttachTo(session);
            session._items.Add(item);
        }

        session.RecordTransition(
            fromState: null,
            NavigatingState,
            TripStateHistory.NavigationStartedReason,
            TripStateHistory.TriggeredByTraveler,
            startedAtUtc);
        return session;
    }

    public TripSessionProgressOutcome ReachItem(long itineraryItemId, DateTimeOffset reachedAtUtc)
    {
        EnsureUtc(reachedAtUtc, nameof(reachedAtUtc));
        var target = FindItem(itineraryItemId);
        if (target is null)
        {
            return TripSessionProgressOutcome.NotInSnapshot;
        }

        if (target.Status == TripSessionItem.ReachedStatus)
        {
            return TripSessionProgressOutcome.Replayed;
        }

        if (IsCompleted)
        {
            return TripSessionProgressOutcome.SessionCompleted;
        }

        target.MarkReached(reachedAtUtc);
        ExploringItemId = itineraryItemId;
        if (FsmState != ExploringState)
        {
            RecordTransition(
                FsmState,
                ExploringState,
                TripStateHistory.ItemReachedReason,
                TripStateHistory.TriggeredByTraveler,
                reachedAtUtc);
        }

        return TripSessionProgressOutcome.Applied;
    }

    public TripSessionProgressOutcome SkipItem(long itineraryItemId, DateTimeOffset skippedAtUtc)
    {
        EnsureUtc(skippedAtUtc, nameof(skippedAtUtc));
        var target = FindItem(itineraryItemId);
        if (target is null)
        {
            return TripSessionProgressOutcome.NotInSnapshot;
        }

        if (target.Status == TripSessionItem.ReachedStatus)
        {
            return TripSessionProgressOutcome.AlreadyReached;
        }

        if (target.Status == TripSessionItem.SkippedStatus)
        {
            return TripSessionProgressOutcome.Replayed;
        }

        if (IsCompleted)
        {
            return TripSessionProgressOutcome.SessionCompleted;
        }

        target.MarkSkipped(skippedAtUtc);
        return TripSessionProgressOutcome.Applied;
    }

    public TripSessionProgressOutcome Depart(DateTimeOffset departedAtUtc)
    {
        EnsureUtc(departedAtUtc, nameof(departedAtUtc));
        if (IsCompleted)
        {
            return TripSessionProgressOutcome.SessionCompleted;
        }

        if (FsmState != ExploringState)
        {
            return TripSessionProgressOutcome.Replayed;
        }

        ExploringItemId = null;
        RecordTransition(
            FsmState,
            NavigatingState,
            TripStateHistory.TravelerDepartedReason,
            TripStateHistory.TriggeredByTraveler,
            departedAtUtc);
        return TripSessionProgressOutcome.Applied;
    }

    public bool Finish(DateTimeOffset finishedAtUtc)
    {
        EnsureUtc(finishedAtUtc, nameof(finishedAtUtc));
        if (IsCompleted)
        {
            return false;
        }

        Complete(
            HasPendingItems ? TravelerStoppedReason : RouteFinishedReason,
            TripStateHistory.TriggeredByTraveler,
            finishedAtUtc);
        return true;
    }

    public bool ExpireIfDue(DateTimeOffset nowUtc)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        if (IsCompleted || ExpiresAtUtc is not { } expiresAtUtc || nowUtc < expiresAtUtc)
        {
            return false;
        }

        Complete(
            HasPendingItems ? ExpiredReason : RouteFinishedReason,
            TripStateHistory.TriggeredBySystem,
            expiresAtUtc);
        return true;
    }

    /// <summary>
    /// Resolves the time of a Traveler action: a device-observed time is kept only when it is not
    /// earlier than the last recorded transition (so a device clock running behind cannot reorder
    /// the history) and not later than the server clock plus the tolerated skew. The state
    /// history must be loaded; without it only the session start bounds the device time.
    /// </summary>
    public DateTimeOffset ResolveEventTime(
        DateTimeOffset? occurredAtUtc,
        DateTimeOffset nowUtc,
        TimeSpan clockSkewTolerance)
    {
        EnsureUtc(nowUtc, nameof(nowUtc));
        if (occurredAtUtc is not { } occurred)
        {
            return nowUtc;
        }

        var occurredUtc = occurred.ToUniversalTime();
        var earliestAllowed = _stateHistory.Count == 0
            ? StartedAtUtc
            : _stateHistory.Max(history => history.ChangedAtUtc);
        if ((earliestAllowed is { } earliest && occurredUtc < earliest)
            || occurredUtc > nowUtc + clockSkewTolerance)
        {
            return nowUtc;
        }

        return occurredUtc < nowUtc ? occurredUtc : nowUtc;
    }

    private TripSessionItem? FindItem(long itineraryItemId) =>
        _items.SingleOrDefault(item => item.ItineraryItemId == itineraryItemId);

    private void Complete(string reason, string triggeredBy, DateTimeOffset changedAtUtc)
    {
        CompletionReason = reason;
        EndedAtUtc = changedAtUtc;
        ExploringItemId = null;
        RecordTransition(FsmState, CompletedState, reason, triggeredBy, changedAtUtc);
    }

    private void RecordTransition(
        string? fromState,
        string toState,
        string reason,
        string triggeredBy,
        DateTimeOffset changedAtUtc)
    {
        FsmState = toState;
        _stateHistory.Add(TripStateHistory.Transition(
            this,
            fromState,
            toState,
            reason,
            triggeredBy,
            changedAtUtc));
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        }
    }
}

public enum TripSessionProgressOutcome
{
    Applied = 1,
    Replayed = 2,
    NotInSnapshot = 3,
    AlreadyReached = 4,
    SessionCompleted = 5,
}
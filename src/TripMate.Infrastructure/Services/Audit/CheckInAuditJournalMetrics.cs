namespace TripMate.Infrastructure.Services.Audit;

public sealed class CheckInAuditJournalMetrics
{
    private long _projected, _duplicates, _projectionFailures, _conflicts, _acknowledgementMisses;
    public long Projected => Interlocked.Read(ref _projected);
    public long Duplicates => Interlocked.Read(ref _duplicates);
    public long ProjectionFailures => Interlocked.Read(ref _projectionFailures);
    public long Conflicts => Interlocked.Read(ref _conflicts);
    public long AcknowledgementMisses => Interlocked.Read(ref _acknowledgementMisses);
    internal void RecordProjected() => Interlocked.Increment(ref _projected);
    internal void RecordDuplicate() => Interlocked.Increment(ref _duplicates);
    internal void RecordFailure() => Interlocked.Increment(ref _projectionFailures);
    internal void RecordConflict() => Interlocked.Increment(ref _conflicts);
    internal void RecordAcknowledgementMiss() => Interlocked.Increment(ref _acknowledgementMisses);
}
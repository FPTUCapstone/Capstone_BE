using TripMate.Application.Features.Scheduling.Csp;

namespace TripMate.Application.UnitTests.Features.Scheduling.Csp;

internal sealed class ManualSolveDeadlineSource : CspItinerarySolver.ISolveDeadlineSource
{
    private readonly CancellationTokenSource _deadlineSource = new();
    private readonly CancellationTokenSource _linkedSource;

    public ManualSolveDeadlineSource(CancellationToken callerToken = default)
    {
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            _deadlineSource.Token);
    }

    public long ElapsedMilliseconds { get; private set; }

    public CancellationToken Token => _linkedSource.Token;

    public bool Disposed { get; private set; }

    public void AdvanceTo(long elapsedMilliseconds)
    {
        if (elapsedMilliseconds < ElapsedMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsedMilliseconds),
                elapsedMilliseconds,
                "Elapsed time must be monotonic.");
        }

        ElapsedMilliseconds = elapsedMilliseconds;
    }

    public void SignalDeadline()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _deadlineSource.Cancel();
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        _linkedSource.Dispose();
        _deadlineSource.Dispose();
    }
}
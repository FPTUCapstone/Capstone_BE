namespace TripMate.Application.Features.TripReviews.Media;

public sealed class ReviewMediaRecoveryRunner(IReviewMediaRecoveryJournal journal, IReviewMediaStorage storage, TimeProvider time)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await journal.ReconcileAbandonedAsync(time.GetUtcNow(), 20, ct);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // Claim just before I/O: preclaiming the entire run would consume later
            // operations' two-minute leases while earlier provider calls execute.
            var claims = await journal.ClaimCleanupAsync(time.GetUtcNow(), 1, ct);
            if (claims.Count == 0) break;
            var claim = claims[0];
            // This parameter carries newly observed provider completion only. The
            // journal retains NeverDispatched/Rejected/Succeeded evidence itself.
            var terminal = false;
            ReviewMediaStorageOutcome outcome;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var probe = await storage.ProbeAsync(claim.PublicId, timeout.Token);
                outcome = probe.Outcome;
                if (probe.Outcome == ReviewMediaStorageOutcome.Success)
                {
                    // One dispatch per operation: existence proves that dispatch finished creating
                    // this exact asset. Ordinary absence can never establish that fact.
                    terminal = true;
                    var deleted = await storage.DestroyAsync(claim.PublicId, timeout.Token);
                    outcome = deleted.Outcome;
                    if (deleted.Outcome is ReviewMediaStorageOutcome.Success or ReviewMediaStorageOutcome.AlreadyAbsent)
                    {
                        var confirmation = await storage.ProbeAsync(claim.PublicId, timeout.Token);
                        outcome = confirmation.Outcome == ReviewMediaStorageOutcome.Success
                            ? ReviewMediaStorageOutcome.UnknownOutcome : confirmation.Outcome;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { outcome = ReviewMediaStorageOutcome.UnknownOutcome; }
            await journal.CompleteCleanupAsync(claim, outcome, terminal, time.GetUtcNow(), ct);
        }
    }
}
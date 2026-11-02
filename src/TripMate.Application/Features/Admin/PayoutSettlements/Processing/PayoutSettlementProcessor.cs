using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.PayoutSettlements.Processing;

public sealed record PayoutSettlementSweepResult(int ItemsHandled, IReadOnlyList<long> FailedWorkItemIds);

/// <summary>One pass over the due payout-settlement work, as the hosted service runs it.</summary>
public interface IPayoutSettlementSweeper
{
    Task<PayoutSettlementSweepResult> ProcessDueAsync(CancellationToken cancellationToken);
}

/// <summary>
/// UC-65 worker. The Administrator's authorization queued one durable work item per payout; this
/// processor takes due items under a lease, pays through <see cref="IPayoutDisbursementGateway"/>,
/// and records the outcome: Settled, SettlementFailed, or, when it can no longer tell, a manual
/// reconciliation that leaves the payout in SettlementProcessing.
///
/// The invariants it protects:
/// - A payout is never submitted twice. Any submission after the first, and any doubt about whether
///   a previous attempt created the transfer, is resolved by looking the payout up by reference first.
/// - A timeout or an unknown gateway state never marks a payout failed. Only a definitive rejection,
///   an explicit FAILED/CANCELLED state, or an empty lookup after the submit attempts are exhausted does.
/// - The plain account number lives only inside one submit call and is never logged or stored.
/// - The gateway is called outside any database transaction; outcomes are written in a second one.
/// </summary>
public sealed class PayoutSettlementProcessor(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock,
    IPayoutDisbursementGateway gateway,
    IPiiProtector piiProtector,
    PayoutSettlementOptions options,
    ILogger<PayoutSettlementProcessor> logger) : IPayoutSettlementSweeper
{
    private enum ResolveCause
    {
        SubmitAttemptsExhausted,
        IdempotencyKeyAlreadyUsed,
    }

    public async Task<PayoutSettlementSweepResult> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await RecoverExpiredLeasesAsync(now, cancellationToken);

        var dueIds = await dbContext.PayoutSettlementWorkItems
            .AsNoTracking()
            .Where(item => (item.State == PayoutSettlementWorkItem.StatePending
                    || item.State == PayoutSettlementWorkItem.StateSubmitted)
                && item.LeaseToken == null
                && item.NextAttemptAtUtc <= now)
            .OrderBy(item => item.NextAttemptAtUtc).ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        var handled = 0;
        var failures = new List<long>();
        foreach (var workItemId in dueIds)
        {
            try
            {
                if (await ProcessItemAsync(workItemId, cancellationToken))
                {
                    handled++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                dbContext.ClearTrackedEntities();
                failures.Add(workItemId);
                logger.LogError(exception, "Payout settlement work item {WorkItemId} failed unexpectedly.", workItemId);
            }
        }

        return new PayoutSettlementSweepResult(handled, failures);
    }

    private async Task RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expiredIds = await dbContext.PayoutSettlementWorkItems
            .AsNoTracking()
            .Where(item => item.LeaseToken != null && item.LeaseExpiresAtUtc <= now)
            .OrderBy(item => item.LeaseExpiresAtUtc).ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var workItemId in expiredIds)
        {
            await dbContext.ExecuteInSerializableTransactionAsync(async token =>
            {
                var item = await dbContext.PayoutSettlementWorkItems.SingleOrDefaultAsync(candidate => candidate.Id == workItemId, token);
                if (item is null || !item.RecoverExpiredLease(now, options.MaxSubmitAttempts))
                {
                    return false;
                }

                if (item.State == PayoutSettlementWorkItem.StateManualReconciliation)
                {
                    AddReconciliationAudit(item.PayoutId, item.LastErrorCode, now);
                    logger.LogError(
                        "Payout {PayoutId} needs manual reconciliation after its worker lease expired on the last submit attempt.",
                        item.PayoutId);
                }

                await dbContext.SaveChangesAsync(token);
                return true;
            }, cancellationToken);
        }
    }

    private async Task<bool> ProcessItemAsync(long workItemId, CancellationToken cancellationToken)
    {
        var claim = await dbContext.ExecuteInSerializableTransactionAsync(
            token => ClaimAsync(workItemId, token), cancellationToken);
        if (claim is null)
        {
            return false;
        }

        if (claim.IsSubmit)
        {
            await SubmitAsync(claim, cancellationToken);
        }
        else
        {
            await PollAsync(claim, cancellationToken);
        }

        return true;
    }

    private async Task<Claim?> ClaimAsync(long workItemId, CancellationToken cancellationToken)
    {
        var item = await dbContext.PayoutSettlementWorkItems.SingleOrDefaultAsync(candidate => candidate.Id == workItemId, cancellationToken);
        var now = clock.UtcNow;
        var token = Guid.NewGuid();
        if (item is null || !item.TryClaim(now, token, options.ProcessingLeaseDuration))
        {
            return null;
        }

        var payoutStatus = await dbContext.Payouts
            .AsNoTracking()
            .Where(payout => payout.Id == item.PayoutId)
            .Select(payout => payout.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (payoutStatus != Payout.StatusSettlementProcessing)
        {
            // Changed outside the worker (for example by an operator): there is nothing left to pay.
            item.Complete(token, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Payout {PayoutId} is no longer SettlementProcessing; its work item was closed without a gateway call.",
                item.PayoutId);
            return null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new Claim(item.Id, item.PayoutId, token, item.State == PayoutSettlementWorkItem.StateSubmitting,
            item.AttemptCount, item.GatewayReference);
    }

    // ----- submit -----

    private async Task SubmitAsync(Claim claim, CancellationToken cancellationToken)
    {
        var beneficiary = await dbContext.Payouts
            .AsNoTracking()
            .Where(payout => payout.Id == claim.PayoutId)
            .Select(payout => new SubmitData(
                payout.PayoutCode, payout.OperatorUserId, payout.NetAmount, payout.BankBin, payout.AccountNumberProtected))
            .SingleAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(beneficiary.BankBin)
            || string.IsNullOrWhiteSpace(beneficiary.AccountNumberProtected)
            || beneficiary.NetAmount <= 0m
            || beneficiary.NetAmount != decimal.Truncate(beneficiary.NetAmount))
        {
            await FailAsync(claim, calls: 0, "beneficiary_incomplete", cancellationToken);
            return;
        }

        var calls = 0;
        if (claim.AttemptNumber > 1)
        {
            // An earlier attempt may have created the transfer (the answer was lost, or the worker
            // crashed). Never send again without asking the gateway first.
            var earlier = await gateway.FindByReferenceAsync(beneficiary.PayoutCode, cancellationToken);
            calls++;
            switch (earlier.Outcome)
            {
                case PayoutLookupOutcome.Found:
                    await AdoptAsync(claim, calls, earlier.GatewayPayoutId!, earlier.State, cancellationToken);
                    return;
                case PayoutLookupOutcome.Failed:
                    await RetryOrReconcileAsync(claim, calls, "lookup_failed", cancellationToken);
                    return;
            }
        }

        string accountNumber;
        try
        {
            accountNumber = piiProtector.Unprotect(beneficiary.AccountNumberProtected);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No detail from the exception: it can come from the key ring, never from the account.
            logger.LogError("The beneficiary account of payout {PayoutId} could not be decrypted.", claim.PayoutId);
            await FailAsync(claim, calls, "beneficiary_unreadable", cancellationToken);
            return;
        }

        var result = await gateway.SubmitAsync(
            new PayoutSubmitRequest(
                beneficiary.PayoutCode,
                beneficiary.NetAmount,
                PayoutDescription.Build(beneficiary.PayoutCode),
                beneficiary.BankBin,
                accountNumber,
                IdempotencyKeyFor(claim.PayoutId)),
            cancellationToken);
        calls++;

        switch (result.Outcome)
        {
            case PayoutSubmitOutcome.Accepted:
                await AdoptAsync(claim, calls, result.GatewayPayoutId!, PayoutGatewayState.Processing, cancellationToken);
                break;
            case PayoutSubmitOutcome.DefinitiveRejection:
                await FailAsync(claim, calls, result.FailureCode ?? "gateway_rejected", cancellationToken);
                break;
            case PayoutSubmitOutcome.IdempotencyKeyExists:
                await ResolveByLookupAsync(claim, calls, beneficiary.PayoutCode, ResolveCause.IdempotencyKeyAlreadyUsed, cancellationToken);
                break;
            default:
                if (claim.AttemptNumber < options.MaxSubmitAttempts)
                {
                    await ScheduleRetryAsync(claim, calls, result.FailureCode ?? "gateway_unavailable", cancellationToken);
                }
                else
                {
                    await ResolveByLookupAsync(claim, calls, beneficiary.PayoutCode, ResolveCause.SubmitAttemptsExhausted, cancellationToken);
                }

                break;
        }
    }

    private async Task ResolveByLookupAsync(
        Claim claim, int calls, string payoutCode, ResolveCause cause, CancellationToken cancellationToken)
    {
        var lookup = await gateway.FindByReferenceAsync(payoutCode, cancellationToken);
        calls++;
        switch (lookup.Outcome)
        {
            case PayoutLookupOutcome.Found:
                await AdoptAsync(claim, calls, lookup.GatewayPayoutId!, lookup.State, cancellationToken);
                break;
            case PayoutLookupOutcome.NotFound when cause == ResolveCause.SubmitAttemptsExhausted:
                // The gateway answered and has no such payout: nothing was created, so nothing can be paid.
                await FailAsync(claim, calls, "gateway_unavailable", cancellationToken);
                break;
            case PayoutLookupOutcome.NotFound:
                await ReconcileAsync(claim, calls, "idempotency_conflict_unresolved", cancellationToken);
                break;
            default:
                await ReconcileAsync(claim, calls, "lookup_failed", cancellationToken);
                break;
        }
    }

    private Task RetryOrReconcileAsync(Claim claim, int calls, string code, CancellationToken cancellationToken) =>
        claim.AttemptNumber < options.MaxSubmitAttempts
            ? ScheduleRetryAsync(claim, calls, code, cancellationToken)
            : ReconcileAsync(claim, calls, code, cancellationToken);

    private async Task ScheduleRetryAsync(Claim claim, int calls, string code, CancellationToken cancellationToken) =>
        await PersistAsync(claim, calls, (payout, item, now) =>
        {
            item.ScheduleSubmitRetry(claim.Token, code, now + options.SubmitRetryDelayAfter(item.AttemptCount));
            return null;
        }, cancellationToken);

    /// <summary>The gateway has (or has just been told of) this transfer. A final state is applied
    /// at once; otherwise the item starts polling.</summary>
    private async Task AdoptAsync(
        Claim claim, int calls, string gatewayPayoutId, PayoutGatewayState state, CancellationToken cancellationToken)
    {
        if (state == PayoutGatewayState.Succeeded)
        {
            await SettleAsync(claim, calls, gatewayPayoutId, cancellationToken);
            return;
        }

        if (TerminalFailureCode(state) is { } failureCode)
        {
            await FailAsync(claim, calls, failureCode, cancellationToken, gatewayPayoutId);
            return;
        }

        await PersistAsync(claim, calls, (payout, item, now) =>
        {
            payout.SetGatewayReference(gatewayPayoutId);
            item.RecordSubmitted(claim.Token, gatewayPayoutId, now, options.PollDeadline, now + options.PollDelayAfter(0));
            return null;
        }, cancellationToken);
    }

    // ----- poll -----

    private async Task PollAsync(Claim claim, CancellationToken cancellationToken)
    {
        var status = await gateway.GetStatusAsync(claim.GatewayReference!, cancellationToken);
        const int calls = 1;
        if (!status.IsAvailable)
        {
            await ContinuePollingAsync(claim, calls, cancellationToken);
            return;
        }

        if (status.State == PayoutGatewayState.Succeeded)
        {
            await SettleAsync(claim, calls, claim.GatewayReference!, cancellationToken);
        }
        else if (TerminalFailureCode(status.State) is { } failureCode)
        {
            await FailAsync(claim, calls, failureCode, cancellationToken);
        }
        else
        {
            await ContinuePollingAsync(claim, calls, cancellationToken);
        }
    }

    /// <summary>The safe failure code of a gateway state that ends the transfer unsuccessfully, or
    /// null for Processing, Succeeded and Unknown (which keep polling).</summary>
    private static string? TerminalFailureCode(PayoutGatewayState state) => state switch
    {
        PayoutGatewayState.Failed => "gateway_failed",
        PayoutGatewayState.Cancelled => "gateway_cancelled",
        PayoutGatewayState.Rejected => "gateway_rejected",
        PayoutGatewayState.Reversed => "gateway_reversed",
        _ => null,
    };

    private async Task ContinuePollingAsync(Claim claim, int calls, CancellationToken cancellationToken)
    {
        var reconcile = false;
        await PersistAsync(claim, calls, (payout, item, now) =>
        {
            if (item.IsPastPollDeadline(now))
            {
                reconcile = true;
                return null;
            }

            item.RecordPoll(claim.Token, now, now + options.PollDelayAfter(item.PollCount + 1));
            return null;
        }, cancellationToken);

        if (reconcile)
        {
            // Calls were already counted above; the reconciliation itself makes none.
            await ReconcileAsync(claim, calls: 0, "poll_deadline_exceeded", cancellationToken);
        }
    }

    // ----- outcomes -----

    private async Task SettleAsync(Claim claim, int calls, string gatewayPayoutId, CancellationToken cancellationToken)
    {
        var notice = await PersistAsync(claim, calls, (payout, item, now) =>
        {
            if (payout.GatewayReference != gatewayPayoutId)
            {
                payout.SetGatewayReference(gatewayPayoutId);
            }

            payout.MarkSettled();
            item.Complete(claim.Token, now);
            AddOutcomeAudit(payout.Id, AuditActionTypes.PayoutSettled, Payout.StatusSettled, null, now);
            return new OutcomeNotice(payout.OperatorUserId, payout.Id, payout.PayoutCode, Settled: true);
        }, cancellationToken);

        await NotifyAsync(notice, cancellationToken);
    }

    private async Task FailAsync(
        Claim claim, int calls, string reasonCode, CancellationToken cancellationToken, string? gatewayPayoutId = null)
    {
        var notice = await PersistAsync(claim, calls, (payout, item, now) =>
        {
            if (gatewayPayoutId is not null && payout.GatewayReference != gatewayPayoutId)
            {
                payout.SetGatewayReference(gatewayPayoutId);
            }

            if (payout.GatewayAttemptCount < 1)
            {
                // The lifecycle rule wants a recorded attempt behind every final outcome.
                payout.RecordGatewayAttempt(now);
            }

            payout.MarkSettlementFailed(reasonCode);
            item.Complete(claim.Token, now);
            AddOutcomeAudit(payout.Id, AuditActionTypes.PayoutSettlementFailed, Payout.StatusSettlementFailed, reasonCode, now);
            return new OutcomeNotice(payout.OperatorUserId, payout.Id, payout.PayoutCode, Settled: false);
        }, cancellationToken);

        await NotifyAsync(notice, cancellationToken);
    }

    private async Task ReconcileAsync(Claim claim, int calls, string reasonCode, CancellationToken cancellationToken)
    {
        await PersistAsync(claim, calls, (payout, item, now) =>
        {
            item.RequireReconciliation(claim.Token, reasonCode);
            AddReconciliationAudit(payout.Id, reasonCode, now);
            logger.LogError(
                "Payout {PayoutId} needs manual reconciliation ({Reason}); gateway reference {GatewayReference}. "
                + "It stays SettlementProcessing and will not be resubmitted.",
                payout.Id, reasonCode, payout.GatewayReference ?? item.GatewayReference ?? "(none)");
            return null;
        }, cancellationToken);
    }

    /// <summary>Applies one outcome to the payout and its work item in a single transaction, but only
    /// while this worker still holds the item's lease. Gateway calls already made are counted first.</summary>
    private Task<OutcomeNotice?> PersistAsync(
        Claim claim,
        int calls,
        Func<Payout, PayoutSettlementWorkItem, DateTimeOffset, OutcomeNotice?> apply,
        CancellationToken cancellationToken) =>
        dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            var item = await dbContext.PayoutSettlementWorkItems.SingleAsync(candidate => candidate.Id == claim.WorkItemId, token);
            if (item.LeaseToken != claim.Token)
            {
                logger.LogWarning(
                    "Work item {WorkItemId} was reclaimed while its gateway call was running; the outcome was discarded.",
                    claim.WorkItemId);
                return null;
            }

            var payout = await dbContext.Payouts.SingleAsync(candidate => candidate.Id == claim.PayoutId, token);
            var now = clock.UtcNow;
            for (var call = 0; call < calls; call++)
            {
                payout.RecordGatewayAttempt(now);
            }

            var notice = apply(payout, item, now);
            await dbContext.SaveChangesAsync(token);
            return notice;
        }, cancellationToken);

    private async Task NotifyAsync(OutcomeNotice? notice, CancellationToken cancellationToken)
    {
        if (notice is null)
        {
            return;
        }

        // The payout outcome is already committed; telling the operator must never undo it.
        try
        {
            dbContext.Notifications.Add(new Notification
            {
                UserId = notice.OperatorUserId,
                Channel = NotificationChannel.InApp,
                Type = notice.Settled ? Notification.PayoutSettledType : Notification.PayoutSettlementFailedType,
                Title = notice.Settled ? "Payout settled" : "Payout could not be completed",
                Body = notice.Settled
                    ? $"Your payout {notice.PayoutCode} has been transferred to your bank account."
                    : $"Your payout {notice.PayoutCode} could not be completed. Please contact TripMate support.",
                RelatedEntityType = Notification.PayoutRelatedEntityType,
                RelatedEntityId = notice.PayoutId,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = clock.UtcNow,
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            dbContext.ClearTrackedEntities();
            logger.LogWarning(exception, "The operator notification for payout {PayoutId} could not be saved.", notice.PayoutId);
        }
    }

    private void AddOutcomeAudit(long payoutId, string action, string finalStatus, string? failureCode, DateTimeOffset now) =>
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            actorUserId: null,
            action,
            AuditEntityTypes.Payout,
            payoutId,
            now,
            AuditOutcome.Success,
            beforeData: JsonSerializer.Serialize(new { Status = Payout.StatusSettlementProcessing }),
            afterData: JsonSerializer.Serialize(new { Status = finalStatus, FailureCode = failureCode })));

    private void AddReconciliationAudit(long payoutId, string? reason, DateTimeOffset now) =>
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            actorUserId: null,
            AuditActionTypes.PayoutReconciliationRequired,
            AuditEntityTypes.Payout,
            payoutId,
            now,
            AuditOutcome.Success,
            beforeData: JsonSerializer.Serialize(new { Status = Payout.StatusSettlementProcessing }),
            afterData: JsonSerializer.Serialize(new { Status = Payout.StatusSettlementProcessing, Reason = reason })));

    /// <summary>The team's stable per-payout key, identical on every attempt.</summary>
    private static string IdempotencyKeyFor(long payoutId) => $"tripmate-payout-{payoutId}";

    private sealed record Claim(
        long WorkItemId, long PayoutId, Guid Token, bool IsSubmit, int AttemptNumber, string? GatewayReference);

    private sealed record SubmitData(
        string PayoutCode, long OperatorUserId, decimal NetAmount, string? BankBin, string? AccountNumberProtected);

    private sealed record OutcomeNotice(long OperatorUserId, long PayoutId, string PayoutCode, bool Settled);
}
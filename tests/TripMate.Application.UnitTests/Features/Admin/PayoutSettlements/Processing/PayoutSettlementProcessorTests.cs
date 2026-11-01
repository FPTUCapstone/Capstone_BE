using System.Security.Cryptography;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.PayoutSettlements.Processing;
using TripMate.Application.UnitTests.Features.Operator.Payouts;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.PayoutSettlements.Processing;

public sealed class PayoutSettlementProcessorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private const string AccountNumber = "1029384756";

    private readonly TestDbContext _db = TestDbContext.Create();
    private readonly FakeDateTimeProvider _clock = new() { UtcNow = T0 };
    private readonly ScriptedGateway _gateway = new();
    private readonly CapturingLogger _logger = new();
    private readonly PayoutSettlementOptions _options = new();
    private readonly PayoutSettlementProcessor _processor;

    public PayoutSettlementProcessorTests()
    {
        _processor = new PayoutSettlementProcessor(_db, _clock, _gateway, new FakePiiProtector(), _options, _logger);
    }

    [Fact]
    public async Task HappyPath_SubmitsOnceThenSettlesOnTheFirstSuccessfulPoll()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));

        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit");
        var request = _gateway.SubmitRequests.Single();
        request.ReferenceId.Should().Be(seed.PayoutCode);
        request.Amount.Should().Be(seed.NetAmount);
        request.Description.Should().Be(PayoutDescription.Build(seed.PayoutCode));
        request.ToBin.Should().Be("970436");
        request.ToAccountNumber.Should().Be(AccountNumber);
        request.IdempotencyKey.Should().Be($"tripmate-payout-{seed.Id}");

        var afterSubmit = await ReloadAsync(seed.Id);
        afterSubmit.Payout.Status.Should().Be(Payout.StatusSettlementProcessing);
        afterSubmit.Payout.GatewayReference.Should().Be("payos-1");
        afterSubmit.Payout.GatewayAttemptCount.Should().Be(1);
        afterSubmit.Item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
        afterSubmit.Item.SubmittedAtUtc.Should().Be(T0);
        afterSubmit.Item.PollDeadlineAtUtc.Should().Be(T0.AddHours(24));
        afterSubmit.Item.NextAttemptAtUtc.Should().Be(T0.AddSeconds(30));

        await _processor.ProcessDueAsync(CancellationToken.None);
        _gateway.Calls.Should().Equal("submit");

        _clock.UtcNow = T0.AddSeconds(30);
        _gateway.Statuses.Enqueue(PayoutStatusResult.Of(PayoutGatewayState.Succeeded));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit", "status");
        var settled = await ReloadAsync(seed.Id);
        settled.Payout.Status.Should().Be(Payout.StatusSettled);
        settled.Payout.GatewayReference.Should().Be("payos-1");
        settled.Payout.GatewayAttemptCount.Should().Be(2);
        settled.Payout.LastGatewayAttemptAtUtc.Should().Be(T0.AddSeconds(30));
        settled.Payout.GatewayFailureReason.Should().BeNull();
        settled.Item.State.Should().Be(PayoutSettlementWorkItem.StateCompleted);
        settled.Item.CompletedAtUtc.Should().Be(T0.AddSeconds(30));

        var audit = _db.AuditLogs.Single(item => item.ActionType == AuditActionTypes.PayoutSettled);
        audit.ActorUserId.Should().BeNull();
        audit.AffectedEntityId.Should().Be(seed.Id);
        var notification = _db.Notifications.Single();
        notification.UserId.Should().Be(seed.OperatorUserId);
        notification.Channel.Should().Be(NotificationChannel.InApp);
        notification.Type.Should().Be("PayoutSettled");
        notification.RelatedEntityType.Should().Be(AuditEntityTypes.Payout);
        notification.RelatedEntityId.Should().Be(seed.Id);
    }

    [Fact]
    public async Task ASettledPayout_ShowsItsSettlementInstantInTheOperatorsHistory()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));
        await _processor.ProcessDueAsync(CancellationToken.None);
        _clock.UtcNow = T0.AddSeconds(30);
        _gateway.Statuses.Enqueue(PayoutStatusResult.Of(PayoutGatewayState.Succeeded));
        await _processor.ProcessDueAsync(CancellationToken.None);

        var history = new TripMate.Application.Features.Operator.Payouts.GetHistory.GetOperatorPayoutHistoryQueryHandler(
            _db, new HistoryUser(seed.OperatorUserId));
        var result = await history.Handle(
            new TripMate.Application.Features.Operator.Payouts.GetHistory.GetOperatorPayoutHistoryQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var item = result.Value.Items.Single();
        item.Status.Should().Be(Payout.StatusSettled);
        item.SettledAtUtc.Should().Be(T0.AddSeconds(30));
        item.Cancellable.Should().BeFalse();
    }

    [Fact]
    public async Task DefinitiveRejection_FailsThePayoutOnceWithASafeReasonAndNeverRetries()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Rejected("gateway_insufficient_balance"));

        await _processor.ProcessDueAsync(CancellationToken.None);
        _clock.UtcNow = T0.AddDays(2);
        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit");
        var failed = await ReloadAsync(seed.Id);
        failed.Payout.Status.Should().Be(Payout.StatusSettlementFailed);
        failed.Payout.GatewayFailureReason.Should().Be("gateway_insufficient_balance");
        failed.Payout.GatewayAttemptCount.Should().Be(1);
        failed.Item.State.Should().Be(PayoutSettlementWorkItem.StateCompleted);
        _db.AuditLogs.Should().ContainSingle(item => item.ActionType == AuditActionTypes.PayoutSettlementFailed);
        var notification = _db.Notifications.Single();
        notification.Type.Should().Be("PayoutSettlementFailed");
        (notification.Title + notification.Body).Should().NotContain("insufficient").And.NotContain("gateway_");
    }

    [Fact]
    public async Task TransientFailure_RetriesLaterAndLooksTheTransferUpBeforeSendingAgain()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Transient("gateway_unavailable"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        var afterFirst = await ReloadAsync(seed.Id);
        afterFirst.Item.State.Should().Be(PayoutSettlementWorkItem.StatePending);
        afterFirst.Item.AttemptCount.Should().Be(1);
        afterFirst.Item.NextAttemptAtUtc.Should().Be(T0.AddMinutes(1));
        afterFirst.Payout.GatewayAttemptCount.Should().Be(1);
        afterFirst.Payout.Status.Should().Be(Payout.StatusSettlementProcessing);

        _clock.UtcNow = T0.AddSeconds(59);
        await _processor.ProcessDueAsync(CancellationToken.None);
        _gateway.Calls.Should().Equal("submit");

        _clock.UtcNow = T0.AddMinutes(1);
        _gateway.Lookups.Enqueue(PayoutLookupResult.NotFound());
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit", "lookup", "submit");
        (await ReloadAsync(seed.Id)).Item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
    }

    [Fact]
    public async Task ARetryThatFindsTheTransferAlreadyCreated_AdoptsItWithoutSubmittingAgain()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Transient("gateway_unavailable"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddMinutes(1);
        _gateway.Lookups.Enqueue(PayoutLookupResult.Found("payos-7", PayoutGatewayState.Processing));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit", "lookup");
        var adopted = await ReloadAsync(seed.Id);
        adopted.Item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
        adopted.Payout.GatewayReference.Should().Be("payos-7");
    }

    [Fact]
    public async Task ARetryThatFindsAnAlreadySucceededTransfer_SettlesItWithoutSubmittingAgain()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Transient("gateway_unavailable"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddMinutes(1);
        _gateway.Lookups.Enqueue(PayoutLookupResult.Found("payos-7", PayoutGatewayState.Succeeded));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit", "lookup");
        (await ReloadAsync(seed.Id)).Payout.Status.Should().Be(Payout.StatusSettled);
    }

    [Fact]
    public async Task ARetryWhoseLookupFails_NeverSubmitsBlindAndTriesAgainLater()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Transient("gateway_unavailable"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddMinutes(1);
        _gateway.Lookups.Enqueue(PayoutLookupResult.Failed());
        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit", "lookup");
        var item = (await ReloadAsync(seed.Id)).Item;
        item.State.Should().Be(PayoutSettlementWorkItem.StatePending);
        item.AttemptCount.Should().Be(2);
        item.LastErrorCode.Should().Be("lookup_failed");
    }

    [Fact]
    public async Task ThreeTransientSubmits_ThenAnEmptyLookup_FailsThePayoutBecauseNothingWasCreated()
    {
        var seed = await SeedAsync();
        await ExhaustSubmitAttemptsAsync();
        _gateway.Lookups.Enqueue(PayoutLookupResult.NotFound());

        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit", "lookup", "submit", "lookup", "submit", "lookup");
        var failed = await ReloadAsync(seed.Id);
        failed.Payout.Status.Should().Be(Payout.StatusSettlementFailed);
        failed.Payout.GatewayFailureReason.Should().Be("gateway_unavailable");
        failed.Item.State.Should().Be(PayoutSettlementWorkItem.StateCompleted);
        failed.Item.AttemptCount.Should().Be(3);
        _db.Notifications.Should().ContainSingle(item => item.Type == "PayoutSettlementFailed");
    }

    [Fact]
    public async Task ThreeTransientSubmits_ThenALookupThatFinds_AdoptsTheTransfer()
    {
        var seed = await SeedAsync();
        await ExhaustSubmitAttemptsAsync();
        _gateway.Lookups.Enqueue(PayoutLookupResult.Found("payos-3", PayoutGatewayState.Processing));

        await _processor.ProcessDueAsync(CancellationToken.None);

        var adopted = await ReloadAsync(seed.Id);
        adopted.Payout.Status.Should().Be(Payout.StatusSettlementProcessing);
        adopted.Payout.GatewayReference.Should().Be("payos-3");
        adopted.Item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
    }

    [Fact]
    public async Task ThreeTransientSubmits_ThenAFailedLookup_LeavesThePayoutForManualReconciliation()
    {
        var seed = await SeedAsync();
        await ExhaustSubmitAttemptsAsync();
        _gateway.Lookups.Enqueue(PayoutLookupResult.Failed());

        await _processor.ProcessDueAsync(CancellationToken.None);

        var stuck = await ReloadAsync(seed.Id);
        stuck.Payout.Status.Should().Be(Payout.StatusSettlementProcessing);
        stuck.Item.State.Should().Be(PayoutSettlementWorkItem.StateManualReconciliation);
        stuck.Item.LastErrorCode.Should().Be("lookup_failed");
        _db.AuditLogs.Should().ContainSingle(item => item.ActionType == AuditActionTypes.PayoutReconciliationRequired);
        _db.Notifications.Should().BeEmpty("the operator is told about outcomes, not about internal reconciliation");
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task IdempotencyKeyAlreadyUsed_LooksUpAndAdoptsTheExistingTransfer()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.KeyExists());
        _gateway.Lookups.Enqueue(PayoutLookupResult.Found("payos-9", PayoutGatewayState.Processing));

        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit", "lookup");
        var adopted = await ReloadAsync(seed.Id);
        adopted.Payout.GatewayReference.Should().Be("payos-9");
        adopted.Item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
    }

    [Theory]
    [InlineData("notfound", "idempotency_conflict_unresolved")]
    [InlineData("failed", "lookup_failed")]
    public async Task IdempotencyKeyAlreadyUsed_ButNoUsableLookup_NeverFailsThePayoutAndWaitsForAPerson(string lookup, string code)
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.KeyExists());
        _gateway.Lookups.Enqueue(lookup == "notfound" ? PayoutLookupResult.NotFound() : PayoutLookupResult.Failed());

        await _processor.ProcessDueAsync(CancellationToken.None);

        var stuck = await ReloadAsync(seed.Id);
        stuck.Payout.Status.Should().Be(Payout.StatusSettlementProcessing);
        stuck.Item.State.Should().Be(PayoutSettlementWorkItem.StateManualReconciliation);
        stuck.Item.LastErrorCode.Should().Be(code);
        _gateway.Calls.Should().Equal("submit", "lookup");
    }

    [Fact]
    public async Task Polling_BacksOffWithIncreasingGapsAndTreatsUnknownAndUnavailableAsStillProcessing()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        var expectedGaps = new[]
        {
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60),
        };
        var results = new[]
        {
            PayoutStatusResult.Of(PayoutGatewayState.Processing),
            PayoutStatusResult.Of(PayoutGatewayState.Unknown),
            PayoutStatusResult.Unavailable(),
            PayoutStatusResult.Of(PayoutGatewayState.Processing),
            PayoutStatusResult.Of(PayoutGatewayState.Unknown),
            PayoutStatusResult.Of(PayoutGatewayState.Processing),
            PayoutStatusResult.Unavailable(),
        };

        var due = T0.AddSeconds(30);
        for (var index = 0; index < results.Length; index++)
        {
            _clock.UtcNow = due;
            _gateway.Statuses.Enqueue(results[index]);
            await _processor.ProcessDueAsync(CancellationToken.None);

            var item = (await ReloadAsync(seed.Id)).Item;
            item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
            item.PollCount.Should().Be(index + 1);
            item.NextAttemptAtUtc.Should().Be(due + expectedGaps[index]);
            due = due + expectedGaps[index];
        }

        (await ReloadAsync(seed.Id)).Payout.Status.Should().Be(Payout.StatusSettlementProcessing);
        _gateway.Calls.Count(call => call == "submit").Should().Be(1);
    }

    [Theory]
    [InlineData(PayoutGatewayState.Failed, "gateway_failed")]
    [InlineData(PayoutGatewayState.Cancelled, "gateway_cancelled")]
    [InlineData(PayoutGatewayState.Rejected, "gateway_rejected")]
    [InlineData(PayoutGatewayState.Reversed, "gateway_reversed")]
    public async Task Polling_AnExplicitFailureState_FailsThePayoutWithASafeCode(PayoutGatewayState state, string code)
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddSeconds(30);
        _gateway.Statuses.Enqueue(PayoutStatusResult.Of(state));
        await _processor.ProcessDueAsync(CancellationToken.None);

        var failed = await ReloadAsync(seed.Id);
        failed.Payout.Status.Should().Be(Payout.StatusSettlementFailed);
        failed.Payout.GatewayFailureReason.Should().Be(code);
        failed.Item.State.Should().Be(PayoutSettlementWorkItem.StateCompleted);
        _db.AuditLogs.Should().Contain(item => item.ActionType == AuditActionTypes.PayoutSettlementFailed);
        _db.Notifications.Should().ContainSingle(item => item.Type == "PayoutSettlementFailed");
        _gateway.Calls.Should().Equal("submit", "status");
    }

    [Theory]
    [InlineData(PayoutGatewayState.Rejected, "gateway_rejected")]
    [InlineData(PayoutGatewayState.Reversed, "gateway_reversed")]
    public async Task ARetryThatFindsTheTransferAlreadyRejectedOrReversed_FailsItWithoutSubmittingAgain(
        PayoutGatewayState state, string code)
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Transient("gateway_unavailable"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddMinutes(1);
        _gateway.Lookups.Enqueue(PayoutLookupResult.Found("payos-7", state));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit", "lookup");
        var failed = await ReloadAsync(seed.Id);
        failed.Payout.Status.Should().Be(Payout.StatusSettlementFailed);
        failed.Payout.GatewayFailureReason.Should().Be(code);
    }

    [Fact]
    public async Task Deadline_AfterTwentyFourHoursWithoutAFinalState_StopsPollingAndNeverFailsOrResubmits()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddHours(24).AddMinutes(1);
        _gateway.Statuses.Enqueue(PayoutStatusResult.Of(PayoutGatewayState.Processing));
        await _processor.ProcessDueAsync(CancellationToken.None);

        var stuck = await ReloadAsync(seed.Id);
        stuck.Payout.Status.Should().Be(Payout.StatusSettlementProcessing);
        stuck.Payout.GatewayFailureReason.Should().BeNull();
        stuck.Item.State.Should().Be(PayoutSettlementWorkItem.StateManualReconciliation);
        stuck.Item.LastErrorCode.Should().Be("poll_deadline_exceeded");
        stuck.Item.GatewayReference.Should().Be("payos-1");
        _db.AuditLogs.Should().ContainSingle(item => item.ActionType == AuditActionTypes.PayoutReconciliationRequired);
        _db.Notifications.Should().BeEmpty();
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);

        _clock.UtcNow = T0.AddDays(5);
        await _processor.ProcessDueAsync(CancellationToken.None);
        _gateway.Calls.Should().Equal("submit", "status");
    }

    [Fact]
    public async Task Deadline_AFinalPollJustAfterTheDeadlineStillSettlesTheTransfer()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddHours(24).AddMinutes(1);
        _gateway.Statuses.Enqueue(PayoutStatusResult.Of(PayoutGatewayState.Succeeded));
        await _processor.ProcessDueAsync(CancellationToken.None);

        (await ReloadAsync(seed.Id)).Payout.Status.Should().Be(Payout.StatusSettled);
    }

    [Fact]
    public async Task ACrashedSubmit_IsRecoveredAndLooksUpBeforeAnyResubmission()
    {
        var seed = await SeedAsync();
        var item = await _db.PayoutSettlementWorkItems.SingleAsync();
        item.TryClaim(T0, Guid.NewGuid(), TimeSpan.FromMinutes(5)).Should().BeTrue();
        await _db.SaveChangesAsync();

        _clock.UtcNow = T0.AddMinutes(1);
        await _processor.ProcessDueAsync(CancellationToken.None);
        _gateway.Calls.Should().BeEmpty("the lease of the crashed worker is still valid");

        _clock.UtcNow = T0.AddMinutes(6);
        _gateway.Lookups.Enqueue(PayoutLookupResult.Found("payos-5", PayoutGatewayState.Processing));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("lookup");
        var recovered = await ReloadAsync(seed.Id);
        recovered.Item.State.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
        recovered.Payout.GatewayReference.Should().Be("payos-5");
    }

    [Fact]
    public async Task ACrashedPoll_BecomesDueAgainAfterItsLeaseExpires()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));
        await _processor.ProcessDueAsync(CancellationToken.None);
        var item = await _db.PayoutSettlementWorkItems.SingleAsync();
        item.TryClaim(T0.AddSeconds(30), Guid.NewGuid(), TimeSpan.FromMinutes(5)).Should().BeTrue();
        await _db.SaveChangesAsync();

        _clock.UtcNow = T0.AddMinutes(6);
        _gateway.Statuses.Enqueue(PayoutStatusResult.Of(PayoutGatewayState.Succeeded));
        await _processor.ProcessDueAsync(CancellationToken.None);

        (await ReloadAsync(seed.Id)).Payout.Status.Should().Be(Payout.StatusSettled);
    }

    [Fact]
    public async Task ASecondSweepRunningWhileTheFirstIsSubmitting_NeverSubmitsTheSamePayoutTwice()
    {
        await SeedAsync();
        var other = new PayoutSettlementProcessor(_db, _clock, _gateway, new FakePiiProtector(), _options, _logger);
        _gateway.OnSubmit = async () => await other.ProcessDueAsync(CancellationToken.None);
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));

        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().Equal("submit");
    }

    [Fact]
    public async Task APayoutThatIsNoLongerProcessing_IsClosedWithoutTouchingTheGateway()
    {
        var seed = await SeedAsync();
        var payout = await _db.Payouts.SingleAsync(item => item.Id == seed.Id);
        typeof(Payout).GetProperty(nameof(Payout.Status))!.SetValue(payout, Payout.StatusSettled);
        await _db.SaveChangesAsync();

        await _processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().BeEmpty();
        (await ReloadAsync(seed.Id)).Item.State.Should().Be(PayoutSettlementWorkItem.StateCompleted);
    }

    [Fact]
    public async Task AnUnreadableAccountNumber_FailsThePayoutWithoutCallingTheGateway()
    {
        var seed = await SeedAsync();
        var processor = new PayoutSettlementProcessor(_db, _clock, _gateway, new ThrowingProtector(), _options, _logger);

        await processor.ProcessDueAsync(CancellationToken.None);

        _gateway.Calls.Should().BeEmpty();
        var failed = await ReloadAsync(seed.Id);
        failed.Payout.Status.Should().Be(Payout.StatusSettlementFailed);
        failed.Payout.GatewayFailureReason.Should().Be("beneficiary_unreadable");
        failed.Payout.GatewayAttemptCount.Should().Be(1, "the lifecycle rule needs a recorded attempt");
    }

    [Fact]
    public async Task AFailureWhileNotifyingTheOperator_DoesNotUndoTheSettlement()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddSeconds(30);
        _gateway.Statuses.Enqueue(PayoutStatusResult.Of(PayoutGatewayState.Succeeded));
        var saves = 0;
        _db.SaveChangesFailureFactory = () => ++saves == 3 ? new DbUpdateException("notification store down") : null;

        await _processor.ProcessDueAsync(CancellationToken.None);

        _db.SaveChangesFailureFactory = null;
        (await ReloadAsync(seed.Id)).Payout.Status.Should().Be(Payout.StatusSettled);
        _db.Notifications.Should().BeEmpty();
        _logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task NothingEverLogsOrStoresThePlainAccountNumber()
    {
        var seed = await SeedAsync();
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-1"));
        await _processor.ProcessDueAsync(CancellationToken.None);
        _clock.UtcNow = T0.AddSeconds(30);
        _gateway.Statuses.Enqueue(PayoutStatusResult.Of(PayoutGatewayState.Succeeded));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _logger.Entries.Select(entry => entry.Message).Should().NotContain(message => message.Contains(AccountNumber));
        _db.AuditLogs.Select(item => item.BeforeData + item.AfterData).Should().NotContain(text => text.Contains(AccountNumber));
        _db.Notifications.Select(item => item.Title + item.Body).Should().NotContain(text => text.Contains(AccountNumber));
        (await ReloadAsync(seed.Id)).Item.LastErrorCode.Should().NotContain(AccountNumber);
    }

    [Fact]
    public async Task OnlyDueItemsAreHandledAndTheBatchSizeIsRespected()
    {
        var small = new PayoutSettlementOptions { BatchSize = 2 };
        var processor = new PayoutSettlementProcessor(_db, _clock, _gateway, new FakePiiProtector(), small, _logger);
        for (var index = 0; index < 4; index++)
        {
            await SeedAsync(operatorId: 100 + index);
            _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted($"payos-{index}"));
        }

        var first = await processor.ProcessDueAsync(CancellationToken.None);
        first.ItemsHandled.Should().Be(2);
        _gateway.Calls.Should().Equal("submit", "submit");

        var second = await processor.ProcessDueAsync(CancellationToken.None);
        second.ItemsHandled.Should().Be(2);
        _gateway.Calls.Count(call => call == "submit").Should().Be(4);
    }

    [Fact]
    public async Task AnUnexpectedErrorOnOneItem_IsReportedAndDoesNotStopTheOthers()
    {
        await SeedAsync(operatorId: 200);
        await SeedAsync(operatorId: 201);
        _gateway.Submits.Enqueue(PayoutSubmitResult.Accepted("payos-ok"));
        _gateway.FailNextSubmitWith = new InvalidOperationException("boom");

        var result = await _processor.ProcessDueAsync(CancellationToken.None);

        result.FailedWorkItemIds.Should().HaveCount(1);
        _gateway.Calls.Count(call => call == "submit").Should().Be(2);
    }

    private async Task ExhaustSubmitAttemptsAsync()
    {
        // Attempt 1 has no lookup (nothing could exist yet); attempts 2 and 3 look first.
        _gateway.Submits.Enqueue(PayoutSubmitResult.Transient("gateway_unavailable"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddMinutes(1);
        _gateway.Lookups.Enqueue(PayoutLookupResult.NotFound());
        _gateway.Submits.Enqueue(PayoutSubmitResult.Transient("gateway_unavailable"));
        await _processor.ProcessDueAsync(CancellationToken.None);

        _clock.UtcNow = T0.AddMinutes(6);
        _gateway.Lookups.Enqueue(PayoutLookupResult.NotFound());
        _gateway.Submits.Enqueue(PayoutSubmitResult.Transient("gateway_unavailable"));
        // The third transient result is followed, in the same pass, by the final lookup the test enqueues.
    }

    private async Task<Payout> SeedAsync(long operatorId = 7)
    {
        var user = await PayoutTestFixture.SeedApprovedOperatorAsync(_db, operatorId);
        var payout = await PayoutTestFixture.SeedPendingReviewPayoutAsync(_db, user.Id, new DateOnly(2026, 9, 1), 12_000_000m, 1_200_000m);
        payout.Approve(5, T0.AddDays(-1));
        payout.AuthorizeSettlement(9, T0.AddMinutes(-1), "970436");
        _db.PayoutSettlementWorkItems.Add(PayoutSettlementWorkItem.Create(payout.Id, T0));
        await _db.SaveChangesAsync();
        return payout;
    }

    private async Task<(Payout Payout, PayoutSettlementWorkItem Item)> ReloadAsync(long payoutId)
    {
        _db.ChangeTracker.Clear();
        var payout = await _db.Payouts.AsNoTracking().SingleAsync(item => item.Id == payoutId);
        var item = await _db.PayoutSettlementWorkItems.AsNoTracking().SingleAsync(candidate => candidate.PayoutId == payoutId);
        return (payout, item);
    }

    private sealed class ScriptedGateway : IPayoutDisbursementGateway
    {
        public Queue<PayoutSubmitResult> Submits { get; } = new();

        public Queue<PayoutStatusResult> Statuses { get; } = new();

        public Queue<PayoutLookupResult> Lookups { get; } = new();

        public List<string> Calls { get; } = [];

        public List<PayoutSubmitRequest> SubmitRequests { get; } = [];

        public Func<Task>? OnSubmit { get; set; }

        public Exception? FailNextSubmitWith { get; set; }

        public async Task<PayoutSubmitResult> SubmitAsync(PayoutSubmitRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("submit");
            SubmitRequests.Add(request);
            if (OnSubmit is { } hook)
            {
                OnSubmit = null;
                await hook();
            }

            if (FailNextSubmitWith is { } failure)
            {
                FailNextSubmitWith = null;
                throw failure;
            }

            return Submits.Count > 0 ? Submits.Dequeue() : throw new InvalidOperationException("Unscripted submit call.");
        }

        public Task<PayoutStatusResult> GetStatusAsync(string gatewayPayoutId, CancellationToken cancellationToken)
        {
            Calls.Add("status");
            return Statuses.Count > 0
                ? Task.FromResult(Statuses.Dequeue())
                : throw new InvalidOperationException("Unscripted status call.");
        }

        public Task<PayoutLookupResult> FindByReferenceAsync(string referenceId, CancellationToken cancellationToken)
        {
            Calls.Add("lookup");
            return Lookups.Count > 0
                ? Task.FromResult(Lookups.Dequeue())
                : throw new InvalidOperationException("Unscripted lookup call.");
        }
    }

    private sealed record HistoryUser(long OperatorId) : ICurrentUserService
    {
        public long? UserId => OperatorId;

        public string? Role => nameof(UserRole.TourOperator);
    }

    private sealed class ThrowingProtector : IPiiProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) => throw new CryptographicException("key ring unavailable");
    }

    private sealed class CapturingLogger : ILogger<PayoutSettlementProcessor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

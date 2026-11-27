using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.Traveler.Payments.Webhook;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Payments;

public sealed class ProcessOperatorPayOsWebhookHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    // The raw checksum key stored in the channel (what FakePaymentCredentialProtector returns
    // after Unprotect). The protected form stored in ChecksumKeyRef is "ref:<rawKey>".
    private const string RawChecksumKey = "checksum-key-operator-1";
    private static readonly string ProtectedChecksumRef = $"ref:{RawChecksumKey}";

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ProcessOperatorPayOsWebhookCommandHandler BuildHandler(
        TestDbContext db,
        FakePaymentCompletionProcessor? completer = null,
        FakePaymentCredentialProtector? protector = null)
    {
        return new ProcessOperatorPayOsWebhookCommandHandler(
            db,
            protector ?? new FakePaymentCredentialProtector(),
            completer ?? new FakePaymentCompletionProcessor());
    }

    private static long SeedReadyChannel(TestDbContext db) =>
        SeedReadyChannel(db, ProtectedChecksumRef);

    private static long SeedReadyChannel(TestDbContext db, string checksumRef)
    {
        var ch = OperatorPaymentChannel.Create(10, Now);
        ch.SetCredentialRefs("ref:client-1", "ref:api-1", checksumRef,
            "123456789", "TO Account", "VCB", Now);
        ch.MarkReady("123456789", "TO Account", "VCB",
            "https://hook.example.com/channel/webhook", Now);
        db.OperatorPaymentChannels.Add(ch);
        db.SaveChangesAsync(default).GetAwaiter().GetResult();
        var booking = Booking.Restore(1, 1, 100, 1, 100_000m, BookingStatus.PendingPayment,
            PaymentStatus.Unpaid, Booking.DefaultCancellationPolicySnapshot);
        booking.StampPaymentFlow(PaymentFlow.OperatorDirect);
        db.Bookings.Add(booking);
        var payment = PaymentTransaction.Create(1, PaymentGateway.PayOS, 100_000m, 20261125001L,
            Now, paymentLinkId: "link-id-001");
        payment.BindOperatorChannel(ch.Id, ch.ChannelVersion, checksumRef);
        db.PaymentTransactions.Add(payment);
        db.SaveChangesAsync(default).GetAwaiter().GetResult();
        return ch.Id;
    }

    private static PayOsWebhookPayload BuildPayload(
        long orderCode,
        decimal amount,
        string checksumKey,
        string code = "00",
        string dataCode = "00")
    {
        var data = new PayOsWebhookData(
            OrderCode: orderCode,
            Amount: amount,
            Description: "Test payment",
            AccountNumber: "123456789",
            Reference: "ref-001",
            TransactionDateTime: "2026-11-25T12:00:00+07:00",
            Currency: "VND",
            PaymentLinkId: "link-id-001",
            Code: dataCode,
            Desc: "Thanh cong");

        var signature = ComputeHmac(data, checksumKey);
        return new PayOsWebhookPayload(Code: code, Desc: "00", Data: data, Signature: signature);
    }

    private static string ComputeHmac(PayOsWebhookData data, string checksumKey)
    {
        var dict = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = ((long)data.Amount).ToString(),
            ["description"] = data.Description ?? string.Empty,
            ["orderCode"] = data.OrderCode.ToString(),
            ["currency"] = data.Currency ?? "VND",
            ["accountNumber"] = data.AccountNumber ?? string.Empty,
            ["reference"] = data.Reference ?? string.Empty,
            ["transactionDateTime"] = data.TransactionDateTime ?? string.Empty,
            ["paymentLinkId"] = data.PaymentLinkId ?? string.Empty,
            ["code"] = data.Code ?? string.Empty,
            ["desc"] = data.Desc ?? string.Empty,
        };

        var sb = new StringBuilder();
        foreach (var (k, v) in dict)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(k).Append('=').Append(v);
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(checksumKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    // ── W1: Valid payload → Success ──────────────────────────────────────────

    [Fact]
    public async Task W1_ValidPayload_ReadyChannel_ValidSignature_ReturnsSuccess()
    {
        await using var db = TestDbContext.Create();
        var channelId = SeedReadyChannel(db);
        var completer = new FakePaymentCompletionProcessor();
        var handler = BuildHandler(db, completer);
        var payload = BuildPayload(20261125001L, 100_000m, RawChecksumKey);

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(channelId, payload), default);

        result.IsSuccess.Should().BeTrue();
        completer.CompleteCallCount.Should().Be(1);
        completer.LastOrderCode.Should().Be(20261125001L);
        completer.LastOutcome.Should().Be(PaymentTransactionStatus.Success);
    }

    // ── W2: Null payload → Failure(CallbackInvalid) ──────────────────────────

    [Fact]
    public async Task W2_NullPayload_ReturnsCallbackInvalid()
    {
        await using var db = TestDbContext.Create();
        var channelId = SeedReadyChannel(db);
        var handler = BuildHandler(db);

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(channelId, null!), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentCallbackInvalid);
    }

    // ── W3: Empty signature → Failure(CallbackInvalid) ───────────────────────

    [Fact]
    public async Task W3_EmptySignature_ReturnsCallbackInvalid()
    {
        await using var db = TestDbContext.Create();
        var channelId = SeedReadyChannel(db);
        var handler = BuildHandler(db);
        var data = new PayOsWebhookData(20261125001L, 100_000m, "desc",
            null, null, null, "VND", null, "00", "ok");
        var payload = new PayOsWebhookPayload("00", "00", data, "");

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(channelId, payload), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentCallbackInvalid);
    }

    // ── W4: Channel not found → Failure(CallbackInvalid) ─────────────────────

    [Fact]
    public async Task W4_ChannelNotFound_ReturnsCallbackInvalid()
    {
        await using var db = TestDbContext.Create();
        var handler = BuildHandler(db);
        var payload = BuildPayload(20261125001L, 100_000m, RawChecksumKey);

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(999, payload), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentCallbackInvalid);
    }

    // ── W5: Channel Disabled (not Ready) → Failure(CallbackInvalid) ──────────

    [Fact]
    public async Task W5_ChannelDisabled_ReturnsCallbackInvalid()
    {
        await using var db = TestDbContext.Create();
        var ch = OperatorPaymentChannel.Create(10, Now);
        ch.SetCredentialRefs("ref:client-1", "ref:api-1", ProtectedChecksumRef,
            "123456789", "TO Account", "VCB", Now);
        ch.MarkReady("123456789", "TO Account", "VCB",
            "https://hook.example.com/channel/webhook", Now);
        ch.Disable(Now);
        db.OperatorPaymentChannels.Add(ch);
        await db.SaveChangesAsync(default);
        var channelId = ch.Id;
        var handler = BuildHandler(db);
        var payload = BuildPayload(20261125001L, 100_000m, RawChecksumKey);

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(channelId, payload), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentCallbackInvalid);
    }

    // ── W6: Wrong signature → Failure(CallbackInvalid) ───────────────────────

    [Fact]
    public async Task W6_WrongSignature_ReturnsCallbackInvalid()
    {
        await using var db = TestDbContext.Create();
        var channelId = SeedReadyChannel(db);
        var handler = BuildHandler(db);
        // Payload signed with a different key than what the channel has
        var payload = BuildPayload(20261125001L, 100_000m, checksumKey: "wrong-key-xyz");

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(channelId, payload), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentCallbackInvalid);
    }

    // ── W7: Credential decrypt fails → Failure(CallbackInvalid) ─────────────

    [Fact]
    public async Task W7_CredentialDecryptFails_ReturnsCallbackInvalid()
    {
        await using var db = TestDbContext.Create();
        // Store a ref that FakePaymentCredentialProtector cannot unprotect (no "ref:" prefix).
        var channelId = SeedReadyChannel(db, checksumRef: "INVALID-NOT-PROTECTED-FORMAT");
        var handler = BuildHandler(db);
        var payload = BuildPayload(20261125001L, 100_000m, RawChecksumKey);

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(channelId, payload), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentCallbackInvalid);
    }

    // ── W8: PaymentNotFound from completer → Success(true) (probe ack) ────────

    [Fact]
    public async Task W8_PaymentNotFoundFromCompleter_AcksProbe()
    {
        await using var db = TestDbContext.Create();
        var channelId = SeedReadyChannel(db);
        var completer = new FakePaymentCompletionProcessor
        {
            ResultToReturn = Application.Common.Models.Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentNotFound, "Transaction not found.")
        };
        var handler = BuildHandler(db, completer);
        var payload = BuildPayload(99999L, 50_000m, RawChecksumKey);

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(channelId, payload), default);

        result.IsSuccess.Should().BeTrue();
    }

    // ── W9: Other completion failure → Failure passthrough ───────────────────

    [Fact]
    public async Task W9_OtherCompletionFailure_ReturnsFailure()
    {
        await using var db = TestDbContext.Create();
        var channelId = SeedReadyChannel(db);
        var completer = new FakePaymentCompletionProcessor
        {
            ResultToReturn = Application.Common.Models.Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Amount mismatch.")
        };
        var handler = BuildHandler(db, completer);
        var payload = BuildPayload(20261125001L, 100_000m, RawChecksumKey);

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(channelId, payload), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PaymentErrorCodes.PaymentCallbackInvalid);
    }

    // ── W10: Non-00 code → Cancelled outcome ─────────────────────────────────

    [Fact]
    public async Task W10_NonSuccessCode_OutcomeIsCancelled()
    {
        await using var db = TestDbContext.Create();
        var channelId = SeedReadyChannel(db);
        var completer = new FakePaymentCompletionProcessor();
        var handler = BuildHandler(db, completer);
        // Cancelled webhook: top-level code "01", data code "01"
        var payload = BuildPayload(20261125001L, 100_000m, RawChecksumKey, code: "01", dataCode: "01");

        var result = await handler.Handle(
            new ProcessOperatorPayOsWebhookCommand(channelId, payload), default);

        result.IsSuccess.Should().BeFalse();
        completer.CompleteCallCount.Should().Be(0);
    }
}

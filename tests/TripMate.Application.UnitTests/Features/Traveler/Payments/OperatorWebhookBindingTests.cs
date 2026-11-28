using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.Traveler.Payments.Webhook;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Payments;

public sealed class OperatorWebhookBindingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SignedRegistrationProbeWorksBeforeApprovalWithoutCompletingAnOrder()
    {
        await using var db = TestDbContext.Create();
        var channel = Channel(db);
        var completion = new FakePaymentCompletionProcessor();
        var result = await Handler(db, completion).Handle(new(channel.Id, Payload()), default);
        result.IsSuccess.Should().BeTrue();
        completion.CompleteCallCount.Should().Be(0);
        db.PaymentTransactions.Should().BeEmpty();
    }

    [Fact]
    public async Task OriginalKeyStillCompletesBoundPaymentAfterChannelChangedAndDisabled()
    {
        await using var db = TestDbContext.Create();
        var channel = Channel(db);
        var payment = Payment(db, channel);
        channel.SetCredentialRefs("ref:new-client", "ref:new-api", "ref:new-key", "999", "TO", "970422", Now);
        channel.Disable(Now);
        await db.SaveChangesAsync();
        var completion = new FakePaymentCompletionProcessor();
        (await Handler(db, completion).Handle(new(channel.Id, Payload()), default)).IsSuccess.Should().BeTrue();
        completion.CompleteCallCount.Should().Be(1);
        payment.OperatorChecksumKeyRef.Should().Be("ref:test-key");
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("amount")]
    [InlineData("link")]
    [InlineData("currency")]
    [InlineData("signature")]
    public async Task AValidSignatureIsNotEnoughForADifferentOrderBinding(string mismatch)
    {
        await using var db = TestDbContext.Create();
        var channel = Channel(db);
        Payment(db, channel);
        var other = Channel(db, 11);
        var completion = new FakePaymentCompletionProcessor();
        var result = await Handler(db, completion).Handle(new(mismatch == "channel" ? other.Id : channel.Id,
            Payload(amount: mismatch == "amount" ? 99 : 100, link: mismatch == "link" ? "other" : "link-1",
                currency: mismatch == "currency" ? "USD" : "VND", key: mismatch == "signature" ? "wrong" : "test-key")), default);
        result.IsSuccess.Should().BeFalse();
        completion.CompleteCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlatformRouteCannotSettleAnOperatorDirectAttempt()
    {
        await using var db = TestDbContext.Create();
        Payment(db, Channel(db));
        var completion = new FakePaymentCompletionProcessor();
        var options = new PaymentOptions();
        options.PayOs.ChecksumKey = "test-key";
        var result = await new ProcessPayOsWebhookCommandHandler(completion, options, db).Handle(new(Payload()), default);
        result.IsSuccess.Should().BeFalse();
        completion.CompleteCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlatformRouteWithoutConfiguredKeyFailsClosed()
    {
        await using var db = TestDbContext.Create();
        var completion = new FakePaymentCompletionProcessor();
        var result = await new ProcessPayOsWebhookCommandHandler(completion, new PaymentOptions(), db).Handle(new(Payload()), default);
        result.IsSuccess.Should().BeFalse();
        completion.CompleteCallCount.Should().Be(0);
    }

    [Fact]
    public async Task NonSuccessEventDoesNotCancelAnExistingPayment()
    {
        await using var db = TestDbContext.Create();
        var channel = Channel(db);
        Payment(db, channel);
        var completion = new FakePaymentCompletionProcessor();
        (await Handler(db, completion).Handle(new(channel.Id, Payload(code: "01")), default)).IsSuccess.Should().BeFalse();
        completion.CompleteCallCount.Should().Be(0);
    }

    private static ProcessOperatorPayOsWebhookCommandHandler Handler(TestDbContext db, FakePaymentCompletionProcessor completion) => new(db, new FakePaymentCredentialProtector(), completion);

    private static OperatorPaymentChannel Channel(TestDbContext db, long owner = 10)
    {
        var channel = OperatorPaymentChannel.Create(owner, Now);
        channel.SetCredentialRefs("ref:client", "ref:api", "ref:test-key", "123", "TO", "970422", Now);
        db.OperatorPaymentChannels.Add(channel);
        db.SaveChangesAsync().GetAwaiter().GetResult();
        return channel;
    }

    private static PaymentTransaction Payment(TestDbContext db, OperatorPaymentChannel channel)
    {
        var booking = Booking.Restore(1, 1, 100, 1, 100, BookingStatus.PendingPayment, PaymentStatus.Unpaid, Booking.DefaultCancellationPolicySnapshot);
        booking.StampPaymentFlow(PaymentFlow.OperatorDirect);
        db.Bookings.Add(booking);
        var payment = PaymentTransaction.Create(1, PaymentGateway.PayOS, 100, 7001, Now, paymentLinkId: "link-1");
        payment.BindOperatorChannel(channel.Id, channel.ChannelVersion, channel.ChecksumKeyRef!);
        db.PaymentTransactions.Add(payment);
        db.SaveChangesAsync().GetAwaiter().GetResult();
        return payment;
    }

    private static PayOsWebhookPayload Payload(decimal amount = 100, string link = "link-1", string currency = "VND", string key = "test-key", string code = "00")
    {
        var data = new PayOsWebhookData(7001, amount, "TM", "123", "reference", "2026-11-25", currency, link, code, "result");
        var properties = JsonSerializer.SerializeToElement(data).EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal);
        var raw = string.Join('&', properties.Select(item => $"{item.Name}={(item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : item.Value.ToString())}"));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return new(code, "result", data, Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw))));
    }
}
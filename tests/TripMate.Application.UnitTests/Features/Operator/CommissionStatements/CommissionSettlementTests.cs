using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.CommissionStatements;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Application.Features.Traveler.Payments.Webhook;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.CommissionStatements;

public sealed class CommissionSettlementTests
{
    [Fact]
    public async Task OperatorWebhookCannotSettleAPlatformCommission()
    {
        await using var db = Db();
        var (s, a) = await Seed(db, PaymentGateway.PayOS);
        var handler = new ProcessOperatorPayOsWebhookCommandHandler(db, new FakePaymentCredentialProtector(), new FakePaymentCompletionProcessor());
        Assert.True((await handler.Handle(new(1, Payload(a, "key")), default)).IsFailure);
        Assert.Equal(DirectCommissionStatement.StatusIssued, s.Status);
    }

    [Fact]
    public async Task MockCannotPayExpiredOrRealProviderAttempt()
    {
        await using var db = Db();
        var (s, a) = await Seed(db, PaymentGateway.Mock);
        var clock = new FakeDateTimeProvider { UtcNow = a.ExpiresAtUtc.AddSeconds(1) };
        var user = new FakeCurrentUserService { UserId = 7, Role = "TourOperator" };
        var handler = new CompleteMockCommissionPaymentCommandHandler(db, user, new PaymentOptions(), clock);
        Assert.True((await handler.Handle(new(s.Id, a.Id), default)).IsFailure);
        Assert.Equal(DirectCommissionStatement.StatusIssued, s.Status);
        var real = CommissionPaymentAttempt.Create(s.Id, PaymentGateway.PayOS, 10000, clock.UtcNow, clock.UtcNow.AddMinutes(15));
        real.AttachLink("real-link", "https://pay.payos.vn/web/real-link", clock.UtcNow);
        db.CommissionPaymentAttempts.Add(real); await db.SaveChangesAsync();
        Assert.True((await handler.Handle(new(s.Id, real.Id), default)).IsFailure);
        Assert.Equal(DirectCommissionStatement.StatusIssued, s.Status);
    }
    [Fact]
    public async Task SignedPlatformCommissionCallbackPaysOnlyItsStatementAndIsIdempotent()
    {
        await using var db = Db();
        var (s, a) = await Seed(db, PaymentGateway.PayOS);
        var bookingCompletion = new FakePaymentCompletionProcessor();
        var options = new PaymentOptions(); options.PayOs.ChecksumKey = "test-commission-key";
        var handler = new ProcessPayOsWebhookCommandHandler(bookingCompletion, options, db);
        var payload = Payload(a, options.PayOs.ChecksumKey);
        Assert.True((await handler.Handle(new(payload), default)).IsSuccess);
        Assert.True((await handler.Handle(new(payload), default)).IsSuccess);
        Assert.Equal(DirectCommissionStatement.StatusPaid, s.Status);
        Assert.Equal(10000, s.PaidAmountVnd);
        Assert.Equal(0, bookingCompletion.CompleteCallCount);
        Assert.Single(db.AuditLogs);
    }

    [Fact]
    public async Task WrongAmountLinkOrSignatureCannotPayACommission()
    {
        await using var db = Db();
        var (s, a) = await Seed(db, PaymentGateway.PayOS);
        var options = new PaymentOptions(); options.PayOs.ChecksumKey = "test-commission-key";
        var handler = new ProcessPayOsWebhookCommandHandler(new FakePaymentCompletionProcessor(), options, db);
        Assert.True((await handler.Handle(new(Payload(a, options.PayOs.ChecksumKey, amount: 1)), default)).IsFailure);
        Assert.True((await handler.Handle(new(Payload(a, options.PayOs.ChecksumKey, link: "other-link")), default)).IsFailure);
        Assert.True((await handler.Handle(new(Payload(a, "wrong-key")), default)).IsFailure);
        Assert.Equal(DirectCommissionStatement.StatusIssued, s.Status);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task MockCompletionRequiresOwnerMockConfigurationAndMockAttempt()
    {
        await using var db = Db();
        var (s, a) = await Seed(db, PaymentGateway.Mock);
        var user = new FakeCurrentUserService { UserId = 8, Role = "TourOperator" };
        var options = new PaymentOptions { Provider = "Mock" };
        var handler = new CompleteMockCommissionPaymentCommandHandler(db, user, options, new FakeDateTimeProvider { UtcNow = DateTimeOffset.UtcNow });
        Assert.True((await handler.Handle(new(s.Id, a.Id), default)).IsFailure);
        user.UserId = 7; options.Provider = "PayOS";
        Assert.True((await handler.Handle(new(s.Id, a.Id), default)).IsFailure);
        options.Provider = "Mock";
        Assert.True((await handler.Handle(new(s.Id, a.Id), default)).IsSuccess);
        Assert.True((await handler.Handle(new(s.Id, a.Id), default)).IsSuccess);
        Assert.Equal(DirectCommissionStatement.StatusPaid, s.Status);
        Assert.Single(db.AuditLogs);
    }
    private static TestDbContext Db() => new(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<(DirectCommissionStatement, CommissionPaymentAttempt)> Seed(TestDbContext db, PaymentGateway gateway)
    {
        var now = DateTimeOffset.UtcNow;
        var statement = DirectCommissionStatement.Issue("DCO-7", 7, "2026-10", 100000, .1m, 10000, new(2026, 11, 1), now);
        db.DirectCommissionStatements.Add(statement); await db.SaveChangesAsync();
        var attempt = CommissionPaymentAttempt.Create(statement.Id, gateway, 10000, now, now.AddMinutes(15));
        attempt.AttachLink("commission-link", gateway == PaymentGateway.PayOS ? "https://pay.payos.vn/web/commission-link" : null, now);
        db.CommissionPaymentAttempts.Add(attempt); await db.SaveChangesAsync();
        return (statement, attempt);
    }
    private static PayOsWebhookPayload Payload(CommissionPaymentAttempt a, string key, decimal? amount = null, string? link = null)
    {
        var data = new PayOsWebhookData(a.OrderCode, amount ?? a.AmountVnd, "HH", "123", "bank-ref-1", "2026-11-27 12:00:00", "VND", link ?? a.PaymentLinkId, "00", "success");
        string canonical = $"accountNumber=123&amount={(long)data.Amount}&code=00&currency=VND&desc=success&description=HH&orderCode={a.OrderCode}&paymentLinkId={data.PaymentLinkId}&reference=bank-ref-1&transactionDateTime=2026-11-27 12:00:00";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return new("00", "success", data, Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))));
    }
}
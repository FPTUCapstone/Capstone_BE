using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.CommissionStatements;

public sealed class CommissionPaymentDomainTests
{
    [Fact]
    public void OrderCodesUseADisjointJavascriptSafeNamespace()
    {
        var now = DateTimeOffset.UtcNow;
        var attempts = Enumerable.Range(0, 100).Select(_ => CommissionPaymentAttempt.Create(1, PaymentGateway.PayOS, 10000, now, now.AddMinutes(15))).ToArray();
        Assert.All(attempts, a => { Assert.True(CommissionPaymentAttempt.IsCommissionOrderCode(a.OrderCode)); Assert.InRange(a.OrderCode, 1L, 9007199254740991L); });
        Assert.Equal(100, attempts.Select(a => a.OrderCode).Distinct().Count());
        Assert.False(CommissionPaymentAttempt.IsCommissionOrderCode(now.ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void StatementCannotBeMarkedPaidWithAnArbitraryAmount()
    {
        var now = DateTimeOffset.UtcNow;
        var statement = DirectCommissionStatement.Issue("DCO-1", 1, "2026-10", 100000, .1m, 10000, new(2026, 11, 1), now);
        Assert.Throws<ArgumentOutOfRangeException>(() => statement.MarkPaid(1, now, now));
        statement.MarkPaid(10000, now, now);
        statement.MarkPaid(10000, now.AddDays(1), now.AddDays(1));
        Assert.Equal(now, statement.PaidAtUtc);
    }
}

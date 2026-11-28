using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Frozen calculation and credit balance; zero-due statements are not bank payments.</summary>
public sealed class DirectCommissionCalculation : BaseEntity
{
    private DirectCommissionCalculation() { }
    public long StatementId { get; private set; }
    public DirectCommissionStatement Statement { get; private set; } = null!;
    public long GrossRevenueVnd { get; private set; }
    public long CompletedRefundsVnd { get; private set; }
    public long RevenueCommissionVnd { get; private set; }
    public long RefundCommissionCreditVnd { get; private set; }
    public long CreditBroughtForwardVnd { get; private set; }
    public long CreditAppliedVnd { get; private set; }
    public long CreditCarriedForwardVnd { get; private set; }
    public int HeldBookingCount { get; private set; }
    public int AmbiguousLegacyBookingCount { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static DirectCommissionCalculation Create(DirectCommissionStatement statement, long grossRevenue, long refunds,
        long revenueCommission, long refundCredit, long broughtForward, int held, int ambiguous, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (grossRevenue < 0 || refunds < 0 || revenueCommission < 0 || refundCredit < 0 || broughtForward < 0 || held < 0 || ambiguous < 0)
            throw new ArgumentOutOfRangeException(nameof(grossRevenue));
        long totalCredit = checked(refundCredit + broughtForward);
        return new()
        {
            Statement = statement,
            GrossRevenueVnd = grossRevenue,
            CompletedRefundsVnd = refunds,
            RevenueCommissionVnd = revenueCommission,
            RefundCommissionCreditVnd = refundCredit,
            CreditBroughtForwardVnd = broughtForward,
            CreditAppliedVnd = Math.Min(totalCredit, revenueCommission),
            CreditCarriedForwardVnd = Math.Max(0, totalCredit - revenueCommission),
            HeldBookingCount = held,
            AmbiguousLegacyBookingCount = ambiguous,
            CreatedAtUtc = now.ToUniversalTime()
        };
    }
}
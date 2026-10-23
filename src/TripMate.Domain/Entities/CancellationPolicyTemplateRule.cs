namespace TripMate.Domain.Entities;

public sealed class CancellationPolicyTemplateRule
{
    private CancellationPolicyTemplateRule()
    {
    }

    public long Id { get; private set; }
    public long CancellationPolicyTemplateId { get; private set; }
    public CancellationPolicyTemplate Template { get; private set; } = null!;
    public int DaysBeforeDeparture { get; private set; }
    public decimal CancellationFeePercent { get; private set; }

    public static CancellationPolicyTemplateRule Create(int daysBeforeDeparture, decimal cancellationFeePercent)
    {
        if (daysBeforeDeparture < 0) throw new ArgumentOutOfRangeException(nameof(daysBeforeDeparture));
        if (cancellationFeePercent is < 0 or > 100 || cancellationFeePercent != decimal.Round(cancellationFeePercent, 2))
            throw new ArgumentOutOfRangeException(nameof(cancellationFeePercent));
        return new CancellationPolicyTemplateRule
        {
            DaysBeforeDeparture = daysBeforeDeparture,
            CancellationFeePercent = cancellationFeePercent,
        };
    }

    internal void AttachTo(CancellationPolicyTemplate template) => Template = template;
}
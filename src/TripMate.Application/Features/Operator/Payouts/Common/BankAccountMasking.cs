namespace TripMate.Application.Features.Operator.Payouts.Common;

/// <summary>
/// Masks a beneficiary account number for display (UC-46 FE spec: the full number is never
/// returned to the browser). Keeps only the last 4 characters visible, matching common banking
/// UX conventions; the full value lives only in <see cref="TripMate.Domain.Entities.OperatorProfile.AccountNumberProtected"/>.
/// </summary>
public static class BankAccountMasking
{
    private const int VisibleSuffixLength = 4;

    public static string Mask(string accountNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        var trimmed = accountNumber.Trim();
        if (trimmed.Length <= VisibleSuffixLength)
        {
            return new string('*', trimmed.Length);
        }

        var suffix = trimmed[^VisibleSuffixLength..];
        return new string('*', trimmed.Length - VisibleSuffixLength) + suffix;
    }
}
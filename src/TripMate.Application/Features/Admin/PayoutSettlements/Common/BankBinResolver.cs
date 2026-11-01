using System.Text;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Admin.PayoutSettlements.Common;

/// <summary>One row of the standard NAPAS/VietQR bank catalogue (<c>payment.Banks</c>).</summary>
public sealed record BankCatalogueEntry(string BankBin, string ShortName, string OfficialName);

/// <summary>
/// Resolves the beneficiary bank BIN for a payout (UC-65). A stored BIN must be in the catalogue;
/// otherwise the bank name must match exactly one catalogue bank: Unicode-NFC normalized, trimmed,
/// compared case-insensitively. Nothing approximate is ever accepted: no accent stripping, no
/// partial or similarity matching, no abbreviations. Moving money to a guessed bank is worse than
/// refusing to move it.
/// </summary>
public static class BankBinResolver
{
    public static Result<string> Resolve(
        string? storedBankBin,
        string? bankName,
        IReadOnlyCollection<BankCatalogueEntry> catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        if (!string.IsNullOrEmpty(storedBankBin))
        {
            return catalogue.Any(entry => string.Equals(entry.BankBin, storedBankBin, StringComparison.Ordinal))
                ? Result.Success(storedBankBin)
                : Unresolved();
        }

        if (string.IsNullOrWhiteSpace(bankName))
        {
            return Unresolved();
        }

        var wanted = Normalize(bankName);
        var matchingBins = catalogue
            .Where(entry => Matches(wanted, entry.ShortName) || Matches(wanted, entry.OfficialName))
            .Select(entry => entry.BankBin)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();

        return matchingBins.Count == 1 ? Result.Success(matchingBins[0]) : Unresolved();
    }

    private static bool Matches(string normalizedWanted, string catalogueName) =>
        string.Equals(normalizedWanted, Normalize(catalogueName), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormC).Trim();

    private static Result<string> Unresolved() => Result.Failure<string>(
        PayoutSettlementErrorCodes.BankBinUnresolved,
        PayoutSettlementErrorCodes.BankBinUnresolvedMessage);
}

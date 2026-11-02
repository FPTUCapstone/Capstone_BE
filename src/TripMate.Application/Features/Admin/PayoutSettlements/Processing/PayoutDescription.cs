using System.Globalization;
using System.Text;

namespace TripMate.Application.Features.Admin.PayoutSettlements.Processing;

/// <summary>
/// The transfer content sent with a payout. It must fit the interbank (NAPAS 24/7) content limit
/// and contain no personal data, so it is plain ASCII without diacritics. The full payout code
/// travels separately as the gateway reference.
/// </summary>
public static class PayoutDescription
{
    public const int MaxLength = 25;
    private const string Brand = "TripMate";

    public static string Build(string payoutCode) => Sanitize($"{Brand} Payout {payoutCode}");

    public static string Sanitize(string value)
    {
        var decomposed = (value ?? string.Empty).Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (character is >= ' ' and <= '~')
            {
                builder.Append(character);
            }
        }

        var ascii = builder.ToString().Trim();
        if (ascii.Length > MaxLength)
        {
            ascii = ascii[..MaxLength].TrimEnd();
        }

        return ascii.Length == 0 ? Brand : ascii;
    }
}
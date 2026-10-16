using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Operator.Tours.Common;

/// <summary>Rules shared by the create and update tour-package validators and handlers.</summary>
public static class OperatorTourValidation
{
    /// <summary>A price is a whole VND amount: scale is irrelevant, so 500000.00 is valid.</summary>
    public static bool IsWholeVnd(decimal value) => value == decimal.Truncate(value);

    public static bool IsWholeVnd(decimal? value) => !value.HasValue || IsWholeVnd(value.Value);

    public static bool AreValidDestinationIds(IReadOnlyList<long>? ids) =>
        ids is { Count: > 0 } && ids.Count <= Tour.MaxDestinations &&
        ids.All(id => id > 0) && ids.Distinct().Count() == ids.Count;

    public static bool IsWithinTermsLimit(string? value) =>
        value is null || value.Trim().Length <= Tour.TermsMaxLength;

    /// <summary>
    /// Human readable destination text derived from the selected catalogue names, in the order chosen.
    /// It is display text only; the catalogue links are the source of truth for search.
    /// </summary>
    public static string BuildDestinationSummary(IEnumerable<string> orderedNames)
    {
        string joined = string.Join(", ", orderedNames.Select(name => name.Trim()).Where(name => name.Length > 0));
        if (joined.Length == 0) return string.Empty;
        return joined.Length <= Tour.DestinationSummaryMaxLength
            ? joined
            : string.Concat(joined.AsSpan(0, Tour.DestinationSummaryMaxLength - 1), "\u2026");
    }

    /// <summary>Trim; blank becomes null. Used before hashing so equivalent payloads fingerprint equally.</summary>
    public static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
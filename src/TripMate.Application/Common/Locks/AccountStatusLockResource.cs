using System.Globalization;

namespace TripMate.Application.Common.Locks;

public static class AccountStatusLockResource
{
    private const string Prefix = "TripMate:AccountStatus:";

    public static string Create(long accountId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);

        return Prefix + accountId.ToString(CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<long> Normalize(IReadOnlyCollection<long> accountIds)
    {
        ArgumentNullException.ThrowIfNull(accountIds);

        var normalized = new SortedSet<long>();
        foreach (var accountId in accountIds)
        {
            if (accountId <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(accountIds),
                    "Account IDs must be positive.");
            }

            normalized.Add(accountId);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException(
                "At least one account ID is required.",
                nameof(accountIds));
        }

        return [.. normalized];
    }
}
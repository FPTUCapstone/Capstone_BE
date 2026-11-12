namespace TripMate.Infrastructure.Persistence;

internal static class SqlServerApplicationLockContract
{
    public const string DatabasePrincipal = "TripMateAppLockRole";
    public const int TotalBudgetMilliseconds = 15_000;
    public const int CommandTimeoutSeconds = 20;
}
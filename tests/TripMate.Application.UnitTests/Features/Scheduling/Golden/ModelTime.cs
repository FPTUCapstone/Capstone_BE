namespace TripMate.Application.UnitTests.Features.Scheduling.Golden;

internal static class ModelTime
{
    public static int ToModelMinute(DateTimeOffset startAtUtc, DateTimeOffset t) =>
        (int)Math.Ceiling((t - startAtUtc).TotalMinutes - 1e-9);
}
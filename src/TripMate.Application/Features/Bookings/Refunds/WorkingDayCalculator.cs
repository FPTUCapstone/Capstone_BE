namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>
/// Working-day arithmetic for BR-144/BR-187's "3 working days" deadlines (UC-42 SRS-alignment
/// spec v1.2). Saturday and Sunday are the only non-working days — the project keeps no
/// public-holiday calendar, matching its existing level of scheduling detail.
/// </summary>
public static class WorkingDayCalculator
{
    /// <summary>Returns the instant <paramref name="workingDays"/> whole working days after
    /// <paramref name="start"/>, counting forward from (but never including) <paramref name="start"/>
    /// itself.</summary>
    public static DateTimeOffset AddWorkingDays(DateTimeOffset start, int workingDays)
    {
        if (workingDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingDays), "Working days to add must be positive.");

        DateTimeOffset result = start;
        int remaining = workingDays;
        while (remaining > 0)
        {
            result = result.AddDays(1);
            if (result.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                remaining--;
        }

        return result;
    }
}
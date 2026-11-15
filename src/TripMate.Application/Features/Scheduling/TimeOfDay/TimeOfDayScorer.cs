using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.TimeOfDay;

/// <summary>What the time-of-day rules need to know about a place.</summary>
public sealed record TimeOfDayTraits(
    IndoorOutdoorType IndoorOutdoor,
    bool HasShelter,
    string? CategoryName,
    decimal? ScenicScore,
    IReadOnlyCollection<string> Tags);

/// <summary>
/// Soft time-of-day preferences (REVIEW2 D10). A higher score means the visit sits in a better part of the day:
/// sunrise at the beach, outdoor places in the cool morning, shelter at noon, scenic spots at sunset.
/// Scores only reorder a plan after the solver (see <see cref="TimeOfDayRefiner"/>); they never exclude a place.
/// </summary>
public static class TimeOfDayScorer
{
    public const string BeachCategory = "Beach";
    public const string NatureCategory = "Natural attraction";
    public const string SunriseTag = "Ngắm bình minh";
    public const string PaddleBoardTag = "Chèo SUP";
    public const decimal ScenicThreshold = 8m;

    private static readonly TimeOnly SunriseStart = new(4, 30);
    private static readonly TimeOnly SunriseEnd = new(7, 30);
    private static readonly TimeOnly MorningStart = new(6, 0);
    private static readonly TimeOnly MorningEnd = new(9, 30);
    private static readonly TimeOnly NoonStart = new(11, 0);
    private static readonly TimeOnly NoonEnd = new(14, 0);
    private static readonly TimeOnly SunsetStart = new(16, 30);
    private static readonly TimeOnly SunsetEnd = new(18, 0);

    /// <summary>Score of a visit whose middle falls at <paramref name="localTime"/>.</summary>
    public static int Score(TimeOnly localTime, TimeOfDayTraits traits)
    {
        var score = 0;
        if (In(localTime, SunriseStart, SunriseEnd) && IsSunriseSpot(traits))
        {
            score += 3;
        }

        if (In(localTime, MorningStart, MorningEnd) && IsOutdoorOrNature(traits))
        {
            score += 1;
        }

        if (In(localTime, NoonStart, NoonEnd) && !traits.HasShelter)
        {
            score -= traits.IndoorOutdoor switch
            {
                IndoorOutdoorType.Outdoor => 2,
                IndoorOutdoorType.Mixed => 1,
                _ => 0,
            };
        }

        if (In(localTime, SunsetStart, SunsetEnd) && IsScenic(traits))
        {
            score += 1;
        }

        return score;
    }

    /// <summary>Why the visit sits where it does, or a warning for an unavoidable outdoor noon visit; null otherwise.</summary>
    public static string? Reason(TimeOnly localTime, TimeOfDayTraits traits)
    {
        if (In(localTime, SunriseStart, SunriseEnd) && IsSunriseSpot(traits))
        {
            return traits.Tags.Contains(PaddleBoardTag, StringComparer.OrdinalIgnoreCase)
                ? "Xếp lúc bình minh: trời mát, ngắm mặt trời mọc, chèo SUP"
                : "Xếp lúc bình minh: trời mát, ngắm mặt trời mọc";
        }

        if (In(localTime, MorningStart, MorningEnd) && IsOutdoorOrNature(traits))
        {
            return "Xếp vào buổi sáng khi trời mát, ít đông";
        }

        if (In(localTime, NoonStart, NoonEnd))
        {
            return traits.IndoorOutdoor == IndoorOutdoorType.Indoor || traits.HasShelter
                ? "Xếp vào buổi trưa để tránh nắng"
                : traits.IndoorOutdoor == IndoorOutdoorType.Outdoor
                    ? "Điểm ngoài trời vào giữa trưa, nên mang nón và nước"
                    : null;
        }

        return In(localTime, SunsetStart, SunsetEnd) && IsScenic(traits)
            ? "Xếp vào cuối chiều để ngắm hoàng hôn"
            : null;
    }

    private static bool In(TimeOnly time, TimeOnly start, TimeOnly end) => time >= start && time < end;

    public static bool IsSunriseSpot(TimeOfDayTraits traits) =>
        string.Equals(traits.CategoryName, BeachCategory, StringComparison.OrdinalIgnoreCase)
        || traits.Tags.Contains(SunriseTag, StringComparer.OrdinalIgnoreCase);

    private static bool IsOutdoorOrNature(TimeOfDayTraits traits) =>
        traits.IndoorOutdoor == IndoorOutdoorType.Outdoor
        || string.Equals(traits.CategoryName, NatureCategory, StringComparison.OrdinalIgnoreCase);

    private static bool IsScenic(TimeOfDayTraits traits) =>
        string.Equals(traits.CategoryName, BeachCategory, StringComparison.OrdinalIgnoreCase)
        || traits.ScenicScore >= ScenicThreshold;
}
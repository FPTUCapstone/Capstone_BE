using System.Text;

namespace TripMate.Application.Features.Tours.Search;

public static class TourSearchCriteria
{
    public const int DestinationMaxLength = 300;
    public const long MaximumWholeVndPrice = 9_999_999_999;
    public const int MaximumPageSize = 100;

    private const string VietnamTimeZoneId = "Asia/Ho_Chi_Minh";

    public static string? NormalizeDestination(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        return destination.Trim().Normalize(NormalizationForm.FormC);
    }

    public static bool TryGetDepartureBoundsUtc(
        DateOnly departureDate,
        out DateTimeOffset lowerInclusive,
        out DateTimeOffset upperExclusive)
    {
        lowerInclusive = default;
        upperExclusive = default;

        if (departureDate == DateOnly.MinValue || departureDate == DateOnly.MaxValue)
        {
            return false;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(VietnamTimeZoneId);
            var localStart = DateTime.SpecifyKind(
                departureDate.ToDateTime(TimeOnly.MinValue),
                DateTimeKind.Unspecified);
            var localEnd = DateTime.SpecifyKind(
                departureDate.AddDays(1).ToDateTime(TimeOnly.MinValue),
                DateTimeKind.Unspecified);

            lowerInclusive = new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(localStart, zone),
                TimeSpan.Zero);
            upperExclusive = new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(localEnd, zone),
                TimeSpan.Zero);
            return lowerInclusive < upperExclusive;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
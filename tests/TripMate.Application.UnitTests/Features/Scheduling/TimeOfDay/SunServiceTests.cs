using FluentAssertions;

using TripMate.Application.Features.Scheduling.TimeOfDay;

namespace TripMate.Application.UnitTests.Features.Scheduling.TimeOfDay;

public class SunServiceTests
{
    private static readonly TimeZoneInfo VietnamTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    public static TheoryData<string, double, double, DateOnly, TimeOnly, TimeOnly> BenchmarkSolarReferenceData()
    {
        var data = new TheoryData<string, double, double, DateOnly, TimeOnly, TimeOnly>();

        // Dữ liệu tham chiếu chuẩn lấy từ NOAA Solar Calculator (https://gml.noaa.gov/grad/solcalc/)
        // dựa trên thuật toán Jean Meeus "Astronomical Algorithms", góc khúc xạ khí quyển 0.833°:
        // 21/06/2026 (Hạ chí - Summer Solstice)
        data.Add("Hà Nội (NOAA)", 21.03, 105.85, new DateOnly(2026, 6, 21), new TimeOnly(5, 16), new TimeOnly(18, 41));
        data.Add("Đà Nẵng (NOAA)", 16.05, 108.20, new DateOnly(2026, 6, 21), new TimeOnly(5, 16), new TimeOnly(18, 21));
        data.Add("TP.HCM (NOAA)", 10.78, 106.70, new DateOnly(2026, 6, 21), new TimeOnly(5, 32), new TimeOnly(18, 18));

        // 21/12/2026 (Đông chí - Winter Solstice)
        data.Add("Hà Nội (NOAA)", 21.03, 105.85, new DateOnly(2026, 12, 21), new TimeOnly(6, 29), new TimeOnly(17, 20));
        data.Add("Đà Nẵng (NOAA)", 16.05, 108.20, new DateOnly(2026, 12, 21), new TimeOnly(6, 10), new TimeOnly(17, 20));
        data.Add("TP.HCM (NOAA)", 10.78, 106.70, new DateOnly(2026, 12, 21), new TimeOnly(6, 6), new TimeOnly(17, 36));

        // Dữ liệu đối chiếu độc lập từ timeanddate.com (khác biệt làm tròn/khúc xạ khí quyển địa phương, chấp nhận sai số <= 2 phút):
        data.Add("Hà Nội (timeanddate)", 21.03, 105.85, new DateOnly(2026, 12, 21), new TimeOnly(6, 28), new TimeOnly(17, 20));
        data.Add("TP.HCM (timeanddate)", 10.78, 106.70, new DateOnly(2026, 12, 21), new TimeOnly(6, 7), new TimeOnly(17, 36));
        data.Add("Hà Nội (timeanddate)", 21.03, 105.85, new DateOnly(2026, 6, 21), new TimeOnly(5, 15), new TimeOnly(18, 41));

        return data;
    }

    [Theory]
    [MemberData(nameof(BenchmarkSolarReferenceData))]
    public void Get_ReturnsSunriseAndSunset_WithinTwoMinutesOfReference(
        string city,
        double lat,
        double lng,
        DateOnly date,
        TimeOnly expectedSunrise,
        TimeOnly expectedSunset)
    {
        var (sunrise, sunset) = SunService.Instance.Get(lat, lng, date, VietnamTz);

        var sunriseDiffMinutes = Math.Abs((sunrise.ToTimeSpan() - expectedSunrise.ToTimeSpan()).TotalMinutes);
        var sunsetDiffMinutes = Math.Abs((sunset.ToTimeSpan() - expectedSunset.ToTimeSpan()).TotalMinutes);

        sunriseDiffMinutes.Should().BeLessOrEqualTo(2, $"{city} sunrise on {date} should match NOAA reference within 2 min (actual: {sunrise}, expected: {expectedSunrise})");
        sunsetDiffMinutes.Should().BeLessOrEqualTo(2, $"{city} sunset on {date} should match NOAA reference within 2 min (actual: {sunset}, expected: {expectedSunset})");
    }

    [Fact]
    public void Get_CachesResults_ByRoundedCoordinatesAndDate()
    {
        var service = new SunService();
        var date = new DateOnly(2026, 10, 20);

        // (16.046, 108.204) rounds to (16.0, 108.2)
        var res1 = service.Get(16.046, 108.204, date, VietnamTz);

        // (16.011, 108.249) rounds to (16.0, 108.2) -> should hit same cached entry
        var res2 = service.Get(16.011, 108.249, date, VietnamTz);

        res2.Should().Be(res1);
    }
}

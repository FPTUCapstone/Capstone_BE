using System.Collections.Concurrent;

namespace TripMate.Application.Features.Scheduling.TimeOfDay;

/// <summary>
/// Dịch vụ tính toán giờ bình minh (Sunrise) và hoàng hôn (Sunset) theo thuật toán NOAA Solar Calculator.
/// Kết quả được cache theo tọa độ làm tròn 1 chữ số thập phân và ngày: (round(lat, 1), round(lng, 1), date).
/// </summary>
public sealed class SunService
{
    public static SunService Instance { get; } = new();

    private readonly ConcurrentDictionary<(double Lat, double Lng, DateOnly Date), (TimeOnly Sunrise, TimeOnly Sunset)> _cache = new();

    /// <summary>
    /// Lấy giờ bình minh và hoàng hôn cho tọa độ, ngày và múi giờ chỉ định.
    /// </summary>
    public (TimeOnly Sunrise, TimeOnly Sunset) Get(double lat, double lng, DateOnly date, TimeZoneInfo tz)
    {
        var key = (Math.Round(lat, 1, MidpointRounding.AwayFromZero),
                   Math.Round(lng, 1, MidpointRounding.AwayFromZero),
                   date);

        return _cache.GetOrAdd(key, k => Calculate(k.Lat, k.Lng, k.Date, tz));
    }

    /// <summary>
    /// Tính toán giờ bình minh và hoàng hôn theo thuật toán chuẩn NOAA Solar Calculator (2-pass).
    /// </summary>
    public static (TimeOnly Sunrise, TimeOnly Sunset) Calculate(double lat, double lng, DateOnly date, TimeZoneInfo tz)
    {
        // 1. Julian Day (0h UT)
        int y = date.Year;
        int m = date.Month;
        int d = date.Day;
        if (m <= 2)
        {
            y -= 1;
            m += 12;
        }

        int a = y / 100;
        int b = 2 - a + (a / 4);
        double jd = Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5;

        // Pass 1: Ước tính tại thời điểm Solar Noon
        double t0 = (jd - 2451545.0) / 36525.0;
        double noonUtc1 = 720.0 - 4.0 * lng - CalcEquationOfTime(t0);
        double tNoon = (jd + noonUtc1 / 1440.0 - 2451545.0) / 36525.0;
        double eqTimeNoon = CalcEquationOfTime(tNoon);
        double solarDecNoon = CalcSunDeclination(tNoon);
        double haNoon = ToDeg(CalcHourAngleSunrise(lat, solarDecNoon));

        double riseUtc1 = 720.0 - 4.0 * lng - eqTimeNoon - 4.0 * haNoon;
        double setUtc1 = 720.0 - 4.0 * lng - eqTimeNoon + 4.0 * haNoon;

        // Pass 2: Tinh chỉnh cho Sunrise
        double tRise = (jd + riseUtc1 / 1440.0 - 2451545.0) / 36525.0;
        double eqTimeRise = CalcEquationOfTime(tRise);
        double solarDecRise = CalcSunDeclination(tRise);
        double haRise = ToDeg(CalcHourAngleSunrise(lat, solarDecRise));
        double riseUtc2 = 720.0 - 4.0 * lng - eqTimeRise - 4.0 * haRise;

        // Pass 2: Tinh chỉnh cho Sunset
        double tSet = (jd + setUtc1 / 1440.0 - 2451545.0) / 36525.0;
        double eqTimeSet = CalcEquationOfTime(tSet);
        double solarDecSet = CalcSunDeclination(tSet);
        double haSet = ToDeg(CalcHourAngleSunrise(lat, solarDecSet));
        double setUtc2 = 720.0 - 4.0 * lng - eqTimeSet + 4.0 * haSet;

        // Chuyển sang giờ địa phương theo múi giờ
        double tzOffsetMinutes = tz.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue)).TotalMinutes;
        double riseLocalMinutes = (riseUtc2 + tzOffsetMinutes) % 1440.0;
        if (riseLocalMinutes < 0)
        {
            riseLocalMinutes += 1440.0;
        }

        double setLocalMinutes = (setUtc2 + tzOffsetMinutes) % 1440.0;
        if (setLocalMinutes < 0)
        {
            setLocalMinutes += 1440.0;
        }

        int riseTotalMins = (int)Math.Round(riseLocalMinutes);
        int setTotalMins = (int)Math.Round(setLocalMinutes);

        var sunrise = new TimeOnly(riseTotalMins / 60 % 24, riseTotalMins % 60);
        var sunset = new TimeOnly(setTotalMins / 60 % 24, setTotalMins % 60);

        return (sunrise, sunset);
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;

    private static double CalcGeomMeanLongSun(double t)
    {
        double l0 = 280.46646 + t * (36000.76983 + 0.0003032 * t);
        l0 %= 360.0;
        if (l0 < 0)
        {
            l0 += 360.0;
        }

        return l0;
    }

    private static double CalcGeomMeanAnomalySun(double t) =>
        357.52911 + t * (35999.05029 - 0.0001537 * t);

    private static double CalcEccentricityEarthOrbit(double t) =>
        0.016708634 - t * (0.000042037 + 0.0000001267 * t);

    private static double CalcSunEqOfCenter(double t)
    {
        double m = CalcGeomMeanAnomalySun(t);
        double mrad = ToRad(m);
        return Math.Sin(mrad) * (1.914602 - t * (0.004817 + 0.000014 * t))
            + Math.Sin(2.0 * mrad) * (0.019993 - 0.000101 * t)
            + Math.Sin(3.0 * mrad) * 0.000289;
    }

    private static double CalcSunTrueLong(double t) =>
        CalcGeomMeanLongSun(t) + CalcSunEqOfCenter(t);

    private static double CalcSunApparentLong(double t)
    {
        double o = CalcSunTrueLong(t);
        return o - 0.00569 - 0.00478 * Math.Sin(ToRad(125.04 - 1934.136 * t));
    }

    private static double CalcMeanObliquityOfEcliptic(double t) =>
        23.0 + (26.0 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60.0) / 60.0;

    private static double CalcObliquityCorrection(double t)
    {
        double e0 = CalcMeanObliquityOfEcliptic(t);
        return e0 + 0.00256 * Math.Cos(ToRad(125.04 - 1934.136 * t));
    }

    private static double CalcSunDeclination(double t)
    {
        double e = CalcObliquityCorrection(t);
        double lambda = CalcSunApparentLong(t);
        double sinDelta = Math.Sin(ToRad(e)) * Math.Sin(ToRad(lambda));
        return ToDeg(Math.Asin(sinDelta));
    }

    private static double CalcEquationOfTime(double t)
    {
        double epsilon = CalcObliquityCorrection(t);
        double l0 = CalcGeomMeanLongSun(t);
        double e = CalcEccentricityEarthOrbit(t);
        double m = CalcGeomMeanAnomalySun(t);

        double y = Math.Tan(ToRad(epsilon) / 2.0);
        y *= y;

        double sin2l0 = Math.Sin(2.0 * ToRad(l0));
        double sinm = Math.Sin(ToRad(m));
        double cos2l0 = Math.Cos(2.0 * ToRad(l0));
        double sin4l0 = Math.Sin(4.0 * ToRad(l0));
        double sin2m = Math.Sin(2.0 * ToRad(m));

        double eTime = y * sin2l0 - 2.0 * e * sinm + 4.0 * e * y * sinm * cos2l0
            - 0.5 * y * y * sin4l0 - 1.25 * e * e * sin2m;
        return ToDeg(eTime) * 4.0;
    }

    private static double CalcHourAngleSunrise(double lat, double solarDec)
    {
        double latRad = ToRad(lat);
        double sdRad = ToRad(solarDec);
        double cosHa = Math.Cos(ToRad(90.8333)) / (Math.Cos(latRad) * Math.Cos(sdRad)) - Math.Tan(latRad) * Math.Tan(sdRad);
        if (cosHa > 1.0)
        {
            return 0.0; // Polar night (không có bình minh)
        }

        if (cosHa < -1.0)
        {
            return Math.PI; // Midnight sun (mặt trời không lặn)
        }

        return Math.Acos(cosHa);
    }
}
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp.Objectives;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Application.Features.Scheduling.Routing;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.TimeOfDay;

/// <summary>
/// Khung giờ hiệu lực của một luật TimeFit quy về phút mô hình.
/// </summary>
public readonly record struct TimeFitWindow(int Open, int Close, double Factor);

/// <summary>
/// Triển khai ITimeFitProvider tính toán hệ số thời gian trong ngày thật dựa trên vị trí địa lý,
/// thuật toán mặt trời NOAA và các luật ưu tiên/phạt theo loại hình địa điểm.
/// </summary>
public sealed class RealTimeFitProvider : ITimeFitProvider
{
    private const string BeachCategory = "Beach";
    private const string NatureCategory = "Nature";
    private const string NaturalAttractionCategory = "Natural attraction";
    private const decimal ScenicThreshold = 8m;

    private readonly TimeFitWindow[][] _windows;
    private readonly double[] _maxFactors;

    public RealTimeFitProvider(
        RoutingContext ctx,
        PersonalParams personalParams,
        TimeFitOptions? options = null)
        : this(ctx, personalParams.SunriseFactor, options)
    {
    }

    public RealTimeFitProvider(
        RoutingContext ctx,
        double sunriseFactor = 0.6,
        TimeFitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var opts = options ?? new TimeFitOptions();

        var n = ctx.Nodes.Count;
        _windows = new TimeFitWindow[n][];
        _maxFactors = new double[n];

        var input = ctx.Input;
        var localStart = TimeZoneInfo.ConvertTime(input.StartAtUtc, input.TimeZone);
        var dayCount = (int)((localStart.TimeOfDay.TotalMinutes + input.AvailableMinutes) / 1440) + 1;

        for (var v = 0; v < n; v++)
        {
            var candidate = ctx.Nodes[v].Candidate;
            var nodeWindows = new List<TimeFitWindow>();

            var r1Applies = IsSunriseSpot(candidate);
            var f1 = 1.0 + opts.SunriseBonusFactor * sunriseFactor;

            var r2Applies = IsOutdoorOrNature(candidate);
            var f2 = opts.CoolMorningFactor;

            var (r3Applies, f3) = GetMiddayFactor(candidate, opts);

            var r4Applies = IsSunsetSpot(candidate);
            var f4 = opts.SunsetFactor;

            var r6Applies = IsNightNatureSpot(candidate);
            var f6 = opts.NightNatureFactor;

            for (var day = 0; day < dayCount; day++)
            {
                var date = localStart.Date.AddDays(day);
                var dateOnly = DateOnly.FromDateTime(date);
                var (sunrise, sunset) = SunService.Instance.Get(
                    (double)candidate.Location.Latitude,
                    (double)candidate.Location.Longitude,
                    dateOnly,
                    input.TimeZone);

                var sunriseDt = date.Add(sunrise.ToTimeSpan());
                var sunsetDt = date.Add(sunset.ToTimeSpan());

                // R1 Bình minh: [sunrise - 45', sunrise + 60']
                if (r1Applies && f1 != 1.0)
                {
                    var open = RoutingContext.ToMinutes(input, sunriseDt.AddMinutes(-opts.SunriseBeforeMinutes), ceiling: false);
                    var close = RoutingContext.ToMinutes(input, sunriseDt.AddMinutes(opts.SunriseAfterMinutes), ceiling: false);
                    if (close > open)
                    {
                        nodeWindows.Add(new TimeFitWindow(open, close, f1));
                    }
                }

                // R2 Sáng mát: [06:00, 09:30]
                if (r2Applies)
                {
                    var open = RoutingContext.ToMinutes(input, date.Add(opts.CoolMorningStart.ToTimeSpan()), ceiling: false);
                    var close = RoutingContext.ToMinutes(input, date.Add(opts.CoolMorningEnd.ToTimeSpan()), ceiling: false);
                    if (close > open)
                    {
                        nodeWindows.Add(new TimeFitWindow(open, close, f2));
                    }
                }

                // R3 Trưa nắng: [11:00, 14:30]
                if (r3Applies)
                {
                    var open = RoutingContext.ToMinutes(input, date.Add(opts.MiddayStart.ToTimeSpan()), ceiling: false);
                    var close = RoutingContext.ToMinutes(input, date.Add(opts.MiddayEnd.ToTimeSpan()), ceiling: false);
                    if (close > open)
                    {
                        nodeWindows.Add(new TimeFitWindow(open, close, f3));
                    }
                }

                // R4 Hoàng hôn: [sunset - 90', sunset + 15']
                if (r4Applies)
                {
                    var open = RoutingContext.ToMinutes(input, sunsetDt.AddMinutes(-opts.SunsetBeforeMinutes), ceiling: false);
                    var close = RoutingContext.ToMinutes(input, sunsetDt.AddMinutes(opts.SunsetAfterMinutes), ceiling: false);
                    if (close > open)
                    {
                        nodeWindows.Add(new TimeFitWindow(open, close, f4));
                    }
                }

                // R6 Tối: [sunset + 30', hết horizon]
                if (r6Applies)
                {
                    var open = RoutingContext.ToMinutes(input, sunsetDt.AddMinutes(opts.NightAfterSunsetMinutes), ceiling: false);
                    var close = (day == dayCount - 1)
                        ? ctx.Horizon
                        : RoutingContext.ToMinutes(input, date.AddDays(1).Add(opts.CoolMorningStart.ToTimeSpan()), ceiling: false);
                    if (close > open)
                    {
                        nodeWindows.Add(new TimeFitWindow(open, close, f6));
                    }
                }
            }

            _windows[v] = nodeWindows.ToArray();

            var maxBoost = 1.0;
            if (r1Applies && f1 > 1.0)
            {
                maxBoost *= f1;
            }

            if (r2Applies && f2 > 1.0)
            {
                maxBoost *= f2;
            }

            if (r4Applies && f4 > 1.0)
            {
                maxBoost *= f4;
            }

            _maxFactors[v] = Math.Min(1.5, maxBoost);
        }
    }

    /// <summary>
    /// Tính hệ số TimeFit cho lượt ghé [startMin, endMin] của node (0 allocation).
    /// </summary>
    public double Factor(int node, int startMin, int endMin)
    {
        if ((uint)node >= (uint)_windows.Length)
        {
            return 1.0;
        }

        var windows = _windows[node];
        var duration = endMin - startMin;
        if (duration <= 0 || windows.Length == 0)
        {
            return 1.0;
        }

        var product = 1.0;
        for (var i = 0; i < windows.Length; i++)
        {
            ref readonly var w = ref windows[i];
            var overlapStart = Math.Max(startMin, w.Open);
            var overlapEnd = Math.Min(endMin, w.Close);
            if (overlapEnd > overlapStart)
            {
                var overlapRatio = (double)(overlapEnd - overlapStart) / duration;
                product *= (1.0 + (w.Factor - 1.0) * overlapRatio);
            }
        }

        if (product < 0.2) return 0.2;
        if (product > 1.5) return 1.5;
        return product;
    }

    /// <summary>
    /// Hệ số lớn nhất có thể của node (đã tính sẵn trong constructor, 0 allocation).
    /// </summary>
    public double MaxFactor(int node)
    {
        if ((uint)node >= (uint)_maxFactors.Length)
        {
            return 1.0;
        }

        return _maxFactors[node];
    }

    /// <summary>
    /// Cho phép truy vấn các khung giờ đã tính cho node (phục vụ kiểm thử).
    /// </summary>
    public IReadOnlyList<TimeFitWindow> GetWindows(int node)
    {
        if ((uint)node >= (uint)_windows.Length)
        {
            return Array.Empty<TimeFitWindow>();
        }

        return _windows[node];
    }

    private static bool IsSunriseSpot(GenerationCandidate candidate)
    {
        if (string.Equals(candidate.CategoryName, BeachCategory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (candidate.Tags is not null)
        {
            foreach (var tag in candidate.Tags)
            {
                if (tag.Contains("bình minh", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("sunrise", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return candidate.Name.Contains("bình minh", StringComparison.OrdinalIgnoreCase) ||
               candidate.Name.Contains("sunrise", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOutdoorOrNature(GenerationCandidate candidate)
    {
        if (candidate.IndoorOutdoor == IndoorOutdoorType.Outdoor)
        {
            return true;
        }

        return string.Equals(candidate.CategoryName, NatureCategory, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(candidate.CategoryName, NaturalAttractionCategory, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool Applies, double Factor) GetMiddayFactor(GenerationCandidate candidate, TimeFitOptions options)
    {
        if (candidate.IndoorOutdoor == IndoorOutdoorType.Outdoor && !candidate.HasShelter)
        {
            return (true, options.MiddayOutdoorFactor);
        }

        if (candidate.IndoorOutdoor == IndoorOutdoorType.Mixed)
        {
            return (true, options.MiddayMixedFactor);
        }

        return (false, 1.0);
    }

    private static bool IsSunsetSpot(GenerationCandidate candidate)
    {
        if (string.Equals(candidate.CategoryName, BeachCategory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.ScenicScore >= ScenicThreshold ||
               candidate.ScenicScoreForRanking >= ScenicThreshold;
    }

    private static bool IsNightNatureSpot(GenerationCandidate candidate)
    {
        if (candidate.IndoorOutdoor != IndoorOutdoorType.Outdoor)
        {
            return false;
        }

        return string.Equals(candidate.CategoryName, NatureCategory, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(candidate.CategoryName, NaturalAttractionCategory, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Factory sinh RealTimeFitProvider cho RoutingContext.
/// </summary>
public sealed class RealTimeFitProviderFactory : ITimeFitProviderFactory
{
    private readonly double _sunriseFactor;
    private readonly TimeFitOptions _options;

    public RealTimeFitProviderFactory(PersonalParams personalParams, TimeFitOptions? options = null)
        : this(personalParams.SunriseFactor, options)
    {
    }

    public RealTimeFitProviderFactory(double sunriseFactor = 0.6, TimeFitOptions? options = null)
    {
        _sunriseFactor = sunriseFactor;
        _options = options ?? new TimeFitOptions();
    }

    public ITimeFitProvider Create(RoutingContext ctx) =>
        new RealTimeFitProvider(ctx, _sunriseFactor, _options);
}

using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Scheduling.TimeOfDay;

public sealed class TimeFitOptions
{
    public const string SectionName = "SchedulingGeneration:TimeFit";

    /// <summary>Phiên bản cấu hình tham số TimeFit.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>R1: Hệ số thưởng cơ bản bình minh (f = 1 + SunriseBonusFactor * SunriseFactor).</summary>
    public double SunriseBonusFactor { get; init; } = 0.3;

    /// <summary>R1: Số phút trước bình minh.</summary>
    public int SunriseBeforeMinutes { get; init; } = 45;

    /// <summary>R1: Số phút sau bình minh.</summary>
    public int SunriseAfterMinutes { get; init; } = 60;

    /// <summary>R2: Hệ số sáng mát cho địa điểm ngoài trời hoặc thiên nhiên.</summary>
    public double CoolMorningFactor { get; init; } = 1.10;

    /// <summary>R2: Giờ bắt đầu sáng mát.</summary>
    public TimeOnly CoolMorningStart { get; init; } = new(6, 0);

    /// <summary>R2: Giờ kết thúc sáng mát.</summary>
    public TimeOnly CoolMorningEnd { get; init; } = new(9, 30);

    /// <summary>R3: Hệ số phạt trưa nắng cho điểm ngoài trời không mái che.</summary>
    public double MiddayOutdoorFactor { get; init; } = 0.70;

    /// <summary>R3: Hệ số phạt trưa nắng cho điểm hỗn hợp (Mixed).</summary>
    public double MiddayMixedFactor { get; init; } = 0.85;

    /// <summary>R3: Giờ bắt đầu trưa nắng.</summary>
    public TimeOnly MiddayStart { get; init; } = new(11, 0);

    /// <summary>R3: Giờ kết thúc trưa nắng.</summary>
    public TimeOnly MiddayEnd { get; init; } = new(14, 30);

    /// <summary>R4: Hệ số thưởng hoàng hôn cho bãi biển hoặc điểm có ScenicScore >= 8.</summary>
    public double SunsetFactor { get; init; } = 1.20;

    /// <summary>R4: Số phút trước hoàng hôn.</summary>
    public int SunsetBeforeMinutes { get; init; } = 90;

    /// <summary>R4: Số phút sau hoàng hôn.</summary>
    public int SunsetAfterMinutes { get; init; } = 15;

    /// <summary>R6: Hệ số phạt tối cho điểm thiên nhiên ngoài trời.</summary>
    public double NightNatureFactor { get; init; } = 0.30;

    /// <summary>R6: Số phút sau hoàng hôn để bắt đầu khung giờ tối.</summary>
    public int NightAfterSunsetMinutes { get; init; } = 30;
}

public sealed class TimeFitOptionsValidator : IValidateOptions<TimeFitOptions>
{
    public ValidateOptionsResult Validate(string? name, TimeFitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Version))
        {
            failures.Add($"{nameof(options.Version)} must not be null or empty.");
        }

        if (options.SunriseBonusFactor < 0)
        {
            failures.Add($"{nameof(options.SunriseBonusFactor)} must be non-negative.");
        }

        if (options.SunriseBeforeMinutes < 0)
        {
            failures.Add($"{nameof(options.SunriseBeforeMinutes)} must be non-negative.");
        }

        if (options.SunriseAfterMinutes < 0)
        {
            failures.Add($"{nameof(options.SunriseAfterMinutes)} must be non-negative.");
        }

        if (options.CoolMorningFactor <= 0)
        {
            failures.Add($"{nameof(options.CoolMorningFactor)} must be positive.");
        }

        if (options.CoolMorningEnd <= options.CoolMorningStart)
        {
            failures.Add($"{nameof(options.CoolMorningEnd)} must be greater than {nameof(options.CoolMorningStart)}.");
        }

        if (options.MiddayOutdoorFactor <= 0)
        {
            failures.Add($"{nameof(options.MiddayOutdoorFactor)} must be positive.");
        }

        if (options.MiddayMixedFactor <= 0)
        {
            failures.Add($"{nameof(options.MiddayMixedFactor)} must be positive.");
        }

        if (options.MiddayEnd <= options.MiddayStart)
        {
            failures.Add($"{nameof(options.MiddayEnd)} must be greater than {nameof(options.MiddayStart)}.");
        }

        if (options.SunsetFactor <= 0)
        {
            failures.Add($"{nameof(options.SunsetFactor)} must be positive.");
        }

        if (options.SunsetBeforeMinutes < 0)
        {
            failures.Add($"{nameof(options.SunsetBeforeMinutes)} must be non-negative.");
        }

        if (options.SunsetAfterMinutes < 0)
        {
            failures.Add($"{nameof(options.SunsetAfterMinutes)} must be non-negative.");
        }

        if (options.NightNatureFactor <= 0)
        {
            failures.Add($"{nameof(options.NightNatureFactor)} must be positive.");
        }

        if (options.NightAfterSunsetMinutes < 0)
        {
            failures.Add($"{nameof(options.NightAfterSunsetMinutes)} must be non-negative.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
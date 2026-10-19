using Microsoft.Extensions.Options;
using TripMate.Domain.Enums;

using TripMate.Domain.Constants;

namespace TripMate.Application.Features.Scheduling.Personalization;

/// <summary>
/// Tham số nhịp độ di chuyển.
/// <paramref name="MaxDailyEffort"/> tính theo phút hoạt động thể lực trong ngày.
/// </summary>
public sealed record PaceParams(double LambdaPerMin, int MaxStopsPerDay, int MaxDailyEffort, int RestAfterMin);

public sealed class PersonalizationOptions
{
    public const string SectionName = "SchedulingGeneration:Personalization";

    /// <summary>Phiên bản cấu hình tham số cá nhân hóa, ghi nhận vào log/telemetry của mỗi lần giải.</summary>
    public string Version { get; init; } = "2026.1";

    /// <summary>Bảng tham số theo nhịp độ di chuyển (Relaxed, Moderate, Fast).</summary>
    public Dictionary<TravelerPace, PaceParams> Pace { get; init; } = [];

    /// <summary>Hệ số phạt thời gian chờ / nhàn rỗi (idle penalty).</summary>
    public double Mu { get; init; } = 0.005;

    /// <summary>Hệ số suy giảm biên mặc định (default diminishing marginal utility factor).</summary>
    public double GammaDefault { get; init; } = 0.6;

    /// <summary>Hệ số suy giảm biên cho danh mục yêu thích (loved category factor).</summary>
    public double GammaLoved { get; init; } = 0.85;

    /// <summary>Trọng số phạt chuỗi hoạt động ngoài trời liên tiếp vượt ngưỡng.</summary>
    public double RhoOutdoor { get; init; } = 0.25;

    /// <summary>Trọng số phạt vi phạm quãng nghỉ trưa / nghỉ ngơi.</summary>
    public double RhoRest { get; init; } = 0.20;

    /// <summary>Trọng số phạt vượt quá số điểm dừng tối đa trong ngày.</summary>
    public double RhoStops { get; init; } = 0.30;

    /// <summary>Trọng số phạt vượt quá nỗ lực thể lực tối đa trong ngày (chuẩn hóa theo 60 phút để không biến soft penalty thành hard constraint).</summary>
    public double RhoEffortPerHour { get; init; } = 0.05;

    /// <summary>Bảng ánh xạ từ interest tag sang danh sách tên danh mục chuẩn hóa trong database.</summary>
    public Dictionary<string, string[]> TagToCategory { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PersonalizationOptionsValidator : IValidateOptions<PersonalizationOptions>
{
    public ValidateOptionsResult Validate(string? name, PersonalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Version))
        {
            failures.Add($"{nameof(options.Version)} must not be null or empty.");
        }

        if (options.Pace is null or { Count: 0 })
        {
            failures.Add($"{nameof(options.Pace)} must contain configurations for all TravelerPace levels.");
        }
        else
        {
            // Kiểm tra đủ 3 mức pace
            TravelerPace[] requiredPaces = [TravelerPace.Relaxed, TravelerPace.Moderate, TravelerPace.Fast];
            foreach (var requiredPace in requiredPaces)
            {
                if (!options.Pace.TryGetValue(requiredPace, out var p))
                {
                    failures.Add($"{nameof(options.Pace)} is missing configuration for '{requiredPace}'.");
                }
                else
                {
                    if (p.LambdaPerMin <= 0)
                    {
                        failures.Add($"Pace[{requiredPace}].{nameof(p.LambdaPerMin)} must be positive.");
                    }

                    if (p.MaxStopsPerDay <= 0)
                    {
                        failures.Add($"Pace[{requiredPace}].{nameof(p.MaxStopsPerDay)} must be positive.");
                    }

                    if (p.MaxDailyEffort <= 0)
                    {
                        failures.Add($"Pace[{requiredPace}].{nameof(p.MaxDailyEffort)} must be positive.");
                    }

                    if (p.RestAfterMin <= 0)
                    {
                        failures.Add($"Pace[{requiredPace}].{nameof(p.RestAfterMin)} must be positive.");
                    }
                }
            }

            // Kiểm tra tính đơn điệu nếu có đủ cả 3 mức
            if (options.Pace.TryGetValue(TravelerPace.Relaxed, out var relaxed)
                && options.Pace.TryGetValue(TravelerPace.Moderate, out var moderate)
                && options.Pace.TryGetValue(TravelerPace.Fast, out var fast))
            {
                // Lambda: Relaxed > Moderate > Fast
                if (!(relaxed.LambdaPerMin > moderate.LambdaPerMin && moderate.LambdaPerMin > fast.LambdaPerMin))
                {
                    failures.Add($"LambdaPerMin must satisfy monotonic order: Relaxed ({relaxed.LambdaPerMin}) > Moderate ({moderate.LambdaPerMin}) > Fast ({fast.LambdaPerMin}).");
                }

                // MaxStops: Relaxed < Moderate < Fast
                if (!(relaxed.MaxStopsPerDay < moderate.MaxStopsPerDay && moderate.MaxStopsPerDay < fast.MaxStopsPerDay))
                {
                    failures.Add($"MaxStopsPerDay must satisfy monotonic order: Relaxed ({relaxed.MaxStopsPerDay}) < Moderate ({moderate.MaxStopsPerDay}) < Fast ({fast.MaxStopsPerDay}).");
                }
            }
        }

        if (options.Mu < 0)
        {
            failures.Add($"{nameof(options.Mu)} must be non-negative.");
        }

        if (options.GammaDefault is <= 0 or > 1)
        {
            failures.Add($"{nameof(options.GammaDefault)} must be in range (0, 1].");
        }

        if (options.GammaLoved is <= 0 or > 1)
        {
            failures.Add($"{nameof(options.GammaLoved)} must be in range (0, 1].");
        }

        if (options.RhoOutdoor < 0)
        {
            failures.Add($"{nameof(options.RhoOutdoor)} must be non-negative.");
        }

        if (options.RhoRest < 0)
        {
            failures.Add($"{nameof(options.RhoRest)} must be non-negative.");
        }

        if (options.RhoStops < 0)
        {
            failures.Add($"{nameof(options.RhoStops)} must be non-negative.");
        }

        if (options.RhoEffortPerHour < 0)
        {
            failures.Add($"{nameof(options.RhoEffortPerHour)} must be non-negative.");
        }

        if (options.TagToCategory is not null)
        {
            foreach (var (tag, categories) in options.TagToCategory)
            {
                if (categories is null or { Length: 0 })
                {
                    failures.Add($"TagToCategory['{tag}'] must contain at least one category.");
                    continue;
                }

                foreach (var category in categories)
                {
                    if (string.IsNullOrWhiteSpace(category))
                    {
                        failures.Add($"TagToCategory['{tag}'] contains a null or empty category.");
                    }
                    else if (!PoiCategoryNames.AllCanonical.Contains(category))
                    {
                        failures.Add($"TagToCategory['{tag}'] contains invalid canonical category '{category}'. Must be one of the 8 canonical database POI categories.");
                    }
                }
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

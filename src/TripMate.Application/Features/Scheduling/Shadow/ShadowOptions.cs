using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Scheduling.Shadow;

public sealed class ShadowOptions
{
    public const string SectionName = "SchedulingGeneration:Shadow";

    /// <summary>
    /// Bật/tắt shadow run ngầm chế độ WeightedUtility khi request chính là Lexicographic.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Tỷ lệ mẫu (0.0 đến 1.0). Ví dụ 0.1 là 10% request chạy shadow.
    /// </summary>
    public double SampleRate { get; init; } = 1.0;

    /// <summary>
    /// Thời gian tối đa (ms) cho một lần chạy shadow trước khi hủy để tránh chiếm tài nguyên.
    /// </summary>
    public int TimeoutMs { get; init; } = 5000;
}

public sealed class ShadowOptionsValidator : IValidateOptions<ShadowOptions>
{
    public ValidateOptionsResult Validate(string? name, ShadowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.SampleRate is < 0.0 or > 1.0)
        {
            failures.Add("Shadow:SampleRate must be between 0.0 and 1.0.");
        }

        if (options.TimeoutMs <= 0)
        {
            failures.Add("Shadow:TimeoutMs must be positive.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
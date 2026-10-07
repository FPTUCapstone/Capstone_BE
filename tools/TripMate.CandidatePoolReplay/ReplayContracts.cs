using System.Text.Json.Serialization;

namespace TripMate.CandidatePoolReplay;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplayManifest(
    string SchemaVersion,
    string CodeVersion,
    string Purpose,
    int ProviderCap,
    int MatrixCap,
    int DiversityCoreSize,
    int DiversityReservedSlots,
    decimal CategoryDiversityWeight,
    decimal GeoDiversityWeight,
    string GeoCellDefinition,
    string BackfillOrder,
    int SensitivityProviderCap,
    int SensitivityMatrixCap);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplayScenario(
    string SchemaVersion,
    string ScenarioId,
    string TransportMode,
    string DensityBucket,
    DateTimeOffset StartAtUtc,
    string TimeZoneId,
    int AvailableMinutes,
    decimal? BudgetVnd,
    string RestPreference,
    IReadOnlyList<ReplayMandatoryCandidate> MandatoryCandidates,
    IReadOnlyList<ReplayCandidate> Candidates,
    IReadOnlyList<string> MatrixOrder,
    IReadOnlyList<IReadOnlyList<int>> RouteMinutes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplayCandidate(
    string Id,
    string Category,
    string GeoCell,
    decimal BaseScore,
    decimal? EffectiveScore,
    decimal? ScenicScore,
    decimal? PhotoRating,
    decimal Distance,
    decimal? Cost,
    bool ValidAtFinalize,
    int VisitDurationMinutes,
    IReadOnlyList<ReplayOpeningHours> OpeningHours);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplayMandatoryCandidate(
    string Id,
    string Category,
    string GeoCell,
    int VisitDurationMinutes,
    decimal? Cost,
    IReadOnlyList<ReplayOpeningHours> OpeningHours);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplayOpeningHours(
    byte DayOfWeek,
    TimeOnly OpenTime,
    TimeOnly CloseTime);

public sealed record ReplayOutput(
    string SchemaVersion,
    string ToolVersion,
    string RuntimeVersion,
    string CorpusSha256,
    ReplayManifest Manifest,
    IReadOnlyList<ScenarioReplayResult> Scenarios);

public sealed record ReplayMeasuredOutput(
    ReplayOutput Output,
    ReplayTimingReport Timing);

public sealed record ReplayTimingReport(
    DateTimeOffset GeneratedAtUtc,
    string RuntimeVersion,
    string OperatingSystem,
    int LogicalProcessorCount,
    IReadOnlyList<StrategyTimingSummary> Strategies);

public sealed record StrategyTimingSummary(
    string Strategy,
    string Stage,
    int SampleCount,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double MeanAllocatedBytes);

public sealed record ScenarioReplayResult(
    string ScenarioId,
    int EligibleCount,
    int MandatoryCount,
    string TransportMode,
    string DensityBucket,
    IReadOnlyList<StrategyReplayResult> Strategies);

public sealed record StrategyReplayResult(
    string Strategy,
    string ScoreTrack,
    IReadOnlyList<string> ProviderPool,
    IReadOnlyList<string> MatrixSelection,
    IReadOnlyList<string> PlannedOptional,
    decimal BaseUtilitySum,
    decimal BaseUtilityMean,
    decimal? EffectiveUtilitySum,
    decimal? EffectiveUtilityMean,
    int CategoryCoverage,
    int GeoCoverage,
    int TravelMinutes,
    bool Feasible,
    int MatrixPoints,
    int MatrixElements,
    PairedStrategyDelta? PairedDelta);

public sealed record PairedStrategyDelta(
    decimal BaseUtilitySum,
    decimal BaseUtilityMean,
    decimal? EffectiveUtilitySum,
    decimal? EffectiveUtilityMean,
    int CategoryCoverage,
    int GeoCoverage,
    int TravelMinutes,
    int MatrixPoints,
    int MatrixElements,
    bool VisitSetChanged,
    bool VisitOrderChanged);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReplayManifest))]
[JsonSerializable(typeof(ReplayScenario))]
[JsonSerializable(typeof(ReplayOutput))]
[JsonSerializable(typeof(ReplayTimingReport))]
internal sealed partial class ReplayJsonContext : JsonSerializerContext;
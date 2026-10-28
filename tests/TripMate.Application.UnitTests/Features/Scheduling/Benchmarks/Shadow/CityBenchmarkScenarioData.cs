using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Shadow;

public sealed record BenchmarkPoiData(
    long Id,
    string Name,
    RoutePoint Location,
    string CategoryName,
    int VisitDurationMinutes,
    decimal EstimatedVisitCost,
    IndoorOutdoorType IndoorOutdoor,
    bool HasShelter,
    decimal ScenicScore,
    decimal PhotoRating,
    IReadOnlyList<string> Tags,
    IReadOnlyList<GenerationOpeningHours> OpeningHours);

public sealed record BenchmarkPersonaData(
    string Id,
    string DisplayName,
    TravelerPace Pace,
    RiskToleranceLevel Risk,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> LovedCategories,
    string Description);

public sealed record CityBenchmarkScenarioData(
    string Id,
    string Source,
    bool ExpectedFeasible,
    DateTimeOffset StartAtUtc,
    string TimeZoneId,
    RoutePoint Start,
    RoutePoint End,
    int AvailableMinutes,
    TransportMode TransportMode,
    RestPreference RestPreference,
    decimal BudgetVnd,
    IReadOnlyList<long> MandatoryPoiIds,
    IReadOnlyList<BenchmarkPoiData> Pois,
    IReadOnlyList<BenchmarkPersonaData> Personas);
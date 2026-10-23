namespace TripMate.Application.Features.Admin.StatisticalReports.Common;

public static class StatisticalReportErrorCodes
{
    public const string Forbidden = "statistical_report.forbidden";
    public const string InvalidCriteria = "statistical_report.invalid_criteria";
    public const string PeriodNotClosed = "statistical_report.period_not_closed";
    public const string UnsupportedScope = "statistical_report.unsupported_scope";
}

public static class StatisticalReportValues
{
    public const string All = "ALL";
    public static readonly string[] ReportTypes = ["USER_GROWTH", "BOOKING_VOLUME", "PLATFORM_REVENUE", "OPERATOR_PERFORMANCE", "DESTINATION_POPULARITY"];
    public static readonly string[] BookingTypes = [All, "TOUR_PACKAGE", "COMMERCIAL_SERVICE"];
}

public sealed record StatisticalPeriodDto(string PeriodKey, DateOnly StartDate, DateOnly EndDate, DateTimeOffset ClosedAtUtc);
public sealed record StatisticalMetricDto(string Key, string Label, decimal Value, bool IsCurrency);
public sealed record StatisticalFormulaDto(decimal TourCommission, decimal ServiceCommission, decimal RefundCommissionReversal, decimal NetPlatformRevenue);
public sealed record StatisticalBreakdownDto(string Key, string Label, decimal PrimaryValue, decimal SecondaryValue, decimal TertiaryValue, bool IsCurrency);
public sealed record StatisticalReportDto(
    string ReportType,
    StatisticalPeriodDto Period,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<StatisticalMetricDto> Metrics,
    StatisticalFormulaDto? Formula,
    IReadOnlyList<StatisticalBreakdownDto> Breakdown);

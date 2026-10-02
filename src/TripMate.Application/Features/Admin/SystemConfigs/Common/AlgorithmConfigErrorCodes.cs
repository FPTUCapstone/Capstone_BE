namespace TripMate.Application.Features.Admin.SystemConfigs.Common;

public static class AlgorithmConfigErrorCodes
{
    public const string Forbidden = "admin.algorithm_config_forbidden";

    public const string InvalidValue = "admin.algorithm_config_invalid_value";

    // Review 2026-10-01 (MAJOR-1): missing managed rows are never masked with seeded
    // defaults; the operator must fix the database instead of being shown fabricated data.
    public const string NotInitialized = "admin.algorithm_config_not_initialized";

    public const string NotInitializedMessage =
        "Algorithm configuration is not initialized. Please contact the system administrator.";

    // Review 2026-10-01 (MAJOR-2): an audit-write failure is a system failure, surfaced with
    // the locked MSG127 text through 503 instead of a generic 500.
    public const string AuditUnavailable = "admin.algorithm_config_unavailable";

    public const string AuditUnavailableMessage =
        "TripMate is temporarily unable to process your request. Please check your connection and try again.";
}
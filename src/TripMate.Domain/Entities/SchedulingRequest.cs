using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public class SchedulingRequest : BaseEntity
{
    public const int RequestHashLength = 64;
    public const int TimeZoneIdMaxLength = 100;

    private SchedulingRequest()
    {
    }

    public long TravelerUserId { get; private set; }

    public User TravelerUser { get; private set; } = null!;

    public Guid IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; } = string.Empty;

    public DateTimeOffset StartAtUtc { get; private set; }

    public string TimeZoneId { get; private set; } = string.Empty;

    public decimal StartLatitude { get; private set; }

    public decimal StartLongitude { get; private set; }

    public decimal ExplorationLatitude { get; private set; }

    public decimal ExplorationLongitude { get; private set; }

    public long? EndPointOfInterestId { get; private set; }

    public bool ReturnToStart { get; private set; }

    public int AvailableMinutes { get; private set; }

    public TransportMode TransportMode { get; private set; }

    public decimal SearchRadiusKm { get; private set; }

    public decimal? BudgetVnd { get; private set; }

    public string MandatoryPoiIdsJson { get; private set; } = "[]";

    public RestPreference RestPreference { get; private set; }

    public SchedulingRequestStatus Status { get; private set; }

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? FailureCode { get; private set; }

    public static SchedulingRequest Create(
        long travelerUserId,
        Guid operationKey,
        string requestHash,
        DateTimeOffset startAtUtc,
        string timeZoneId,
        decimal startLatitude,
        decimal startLongitude,
        decimal explorationLatitude,
        decimal explorationLongitude,
        long? endPointOfInterestId,
        bool returnToStart,
        int availableMinutes,
        TransportMode transportMode,
        decimal searchRadiusKm,
        decimal? budgetVnd,
        string mandatoryPoiIdsJson,
        RestPreference restPreference,
        DateTimeOffset requestedAtUtc)
    {
        if (travelerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(travelerUserId));
        }

        if (operationKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(operationKey));
        }

        var normalizedHash = NormalizeRequestHash(requestHash);
        var normalizedTimeZoneId = NormalizeTimeZoneId(timeZoneId);

        if (!Enum.IsDefined(restPreference))
        {
            throw new ArgumentOutOfRangeException(nameof(restPreference));
        }

        ValidateCoordinate(startLatitude, -90m, 90m, nameof(startLatitude));
        ValidateCoordinate(startLongitude, -180m, 180m, nameof(startLongitude));
        ValidateCoordinate(explorationLatitude, -90m, 90m, nameof(explorationLatitude));
        ValidateCoordinate(explorationLongitude, -180m, 180m, nameof(explorationLongitude));
        if (endPointOfInterestId is <= 0 || endPointOfInterestId.HasValue == returnToStart)
        {
            throw new ArgumentException(
                "Exactly one end point option must be selected.",
                nameof(endPointOfInterestId));
        }

        if (availableMinutes is < 60 or > 720)
        {
            throw new ArgumentOutOfRangeException(nameof(availableMinutes));
        }

        if (!Enum.IsDefined(transportMode))
        {
            throw new ArgumentOutOfRangeException(nameof(transportMode));
        }

        if (searchRadiusKm is < 1m or > 50m)
        {
            throw new ArgumentOutOfRangeException(nameof(searchRadiusKm));
        }

        if (budgetVnd is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetVnd));
        }

        if (string.IsNullOrWhiteSpace(mandatoryPoiIdsJson))
        {
            throw new ArgumentException("Mandatory POI IDs are required.", nameof(mandatoryPoiIdsJson));
        }

        return new SchedulingRequest
        {
            TravelerUserId = travelerUserId,
            IdempotencyKey = operationKey,
            RequestHash = normalizedHash,
            StartAtUtc = startAtUtc,
            TimeZoneId = normalizedTimeZoneId,
            StartLatitude = NormalizeCoordinate(startLatitude),
            StartLongitude = NormalizeCoordinate(startLongitude),
            ExplorationLatitude = NormalizeCoordinate(explorationLatitude),
            ExplorationLongitude = NormalizeCoordinate(explorationLongitude),
            EndPointOfInterestId = endPointOfInterestId,
            ReturnToStart = returnToStart,
            AvailableMinutes = availableMinutes,
            TransportMode = transportMode,
            SearchRadiusKm = searchRadiusKm,
            BudgetVnd = budgetVnd,
            MandatoryPoiIdsJson = mandatoryPoiIdsJson,
            RestPreference = restPreference,
            Status = SchedulingRequestStatus.Pending,
            RequestedAtUtc = requestedAtUtc,
        };
    }

    public void Complete(DateTimeOffset completedAtUtc)
    {
        Status = SchedulingRequestStatus.Completed;
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
        FailureCode = null;
    }

    public void FailInfeasible(string failureCode, DateTimeOffset completedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException("A failure code is required.", nameof(failureCode));
        }

        Status = SchedulingRequestStatus.Failed;
        FailureCode = failureCode.Trim();
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
    }

    private static string NormalizeRequestHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != RequestHashLength)
        {
            throw new ArgumentException(
                $"A request hash must contain exactly {RequestHashLength} characters.",
                nameof(value));
        }

        return value;
    }

    private static string NormalizeTimeZoneId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A time zone is required.", nameof(value));
        }

        var normalized = value.Trim();
        if (normalized.Length > TimeZoneIdMaxLength)
        {
            throw new ArgumentException(
                $"A time zone cannot exceed {TimeZoneIdMaxLength} characters.",
                nameof(value));
        }

        return normalized;
    }

    private static void ValidateCoordinate(decimal value, decimal minimum, decimal maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static decimal NormalizeCoordinate(decimal value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

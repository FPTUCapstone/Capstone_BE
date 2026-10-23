namespace TripMate.Domain.Enums;

/// <summary>What a POI's estimated cost pays for, so clients can label it honestly.</summary>
public enum CostKind
{
    /// <summary>Entrance ticket per person (attractions, museums, parks).</summary>
    AdmissionTicket = 1,

    /// <summary>Typical spend per person (restaurants, cafés).</summary>
    AverageSpend = 2,
}
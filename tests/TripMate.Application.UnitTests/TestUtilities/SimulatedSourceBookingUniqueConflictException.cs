namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>
/// Stands in for the SQL Server unique-index violation (2601/2627) on
/// Itineraries.source_booking_id — SqlException cannot be constructed in tests.
/// </summary>
public sealed class SimulatedSourceBookingUniqueConflictException()
    : Exception("Cannot insert duplicate key row in object 'planning.Itineraries' with unique index 'IX_Itineraries_SourceBookingId'.");
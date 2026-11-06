namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>
/// Stands in for the SQL Server unique violation (2601/2627) on
/// EmergencyTourCancellationOperations — SqlException cannot be constructed in tests.
/// </summary>
public sealed class SimulatedEmergencyDecisionUniqueConflictException()
    : Exception("Violation of UNIQUE KEY constraint 'UX_EmergencyTourCancellationOperations_Schedule'.");
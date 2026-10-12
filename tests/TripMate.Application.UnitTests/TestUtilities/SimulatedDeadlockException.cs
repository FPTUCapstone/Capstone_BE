namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>
/// Stands in for a SQL Server deadlock victim; <see cref="TestDbContext.IsDeadlock"/> reports it
/// as retryable.
/// </summary>
public sealed class SimulatedDeadlockException() : Exception("Simulated deadlock victim.");
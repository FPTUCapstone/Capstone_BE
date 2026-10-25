using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

/// <summary>
/// SqlException cannot be constructed here; the positive path is proven against SQL Server by the
/// concurrent view and refresh tests in TravelerTicketsSqlServerTests.
/// </summary>
public sealed class SqlServerTicketQrPersistenceErrorClassifierTests
{
    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    public void NumberBearingNonSqlException_IsNeverAConflict(int number)
    {
        var exception = new DbUpdateException("write failed",
            new FakeSqlException(number, "Cannot insert duplicate key row with unique index 'UX_TicketQrCodes_OneActivePerTicket'."));

        new SqlServerTicketQrPersistenceErrorClassifier().IsOneActiveCodeConflict(exception).Should().BeFalse();
    }

    [Fact]
    public void IndexNameWithoutSqlException_IsNeverAConflict()
    {
        var exception = new DbUpdateException("write failed",
            new Exception("Cannot insert duplicate key row with unique index 'UX_TicketQrCodes_OneActivePerTicket'."));

        new SqlServerTicketQrPersistenceErrorClassifier().IsOneActiveCodeConflict(exception).Should().BeFalse();
    }

    private sealed class FakeSqlException(int number, string message) : Exception(message)
    {
        public int Number { get; } = number;
    }
}
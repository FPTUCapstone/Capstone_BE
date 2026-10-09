using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class SqlServerTripReviewPersistenceErrorClassifierTests
{
    [Theory]
    [InlineData(2601, "UX_TripReviews_CommerceBooking")]
    [InlineData(2627, "UX_TripReviews_ServiceBooking")]
    [InlineData(2601, "UX_TripReviews_Booking")]
    public void ArbitraryNumberBearingException_IsNeverClassifiedAsBookingDuplicate(
        int number, string index)
    {
        var exception = new DbUpdateException("write failed",
            new FakeSqlException(number, $"Violation of UNIQUE KEY constraint '{index}'."));

        new SqlServerTripReviewPersistenceErrorClassifier().IsBookingDuplicate(exception).Should().BeFalse();
    }

    [Fact]
    public void IndexNameWithoutSqlServerException_IsNeverClassifiedAsBookingDuplicate()
    {
        var exception = new DbUpdateException("write failed",
            new Exception("Violation of UNIQUE KEY constraint 'UX_TripReviews_Booking'."));

        new SqlServerTripReviewPersistenceErrorClassifier().IsBookingDuplicate(exception).Should().BeFalse();
    }

    private sealed class FakeSqlException(int number, string message) : Exception(message)
    {
        public int Number { get; } = number;
    }
}
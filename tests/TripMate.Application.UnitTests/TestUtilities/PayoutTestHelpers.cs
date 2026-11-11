using TripMate.Application.Common.Interfaces;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.TestUtilities;

internal static class PayoutTestHelpers
{
    internal static FakeCurrentUserService CurrentUser(long operatorId) =>
        new() { UserId = operatorId, Role = nameof(TripMate.Domain.Enums.UserRole.TourOperator) };

    internal sealed class FixedClock(DateTimeOffset now) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
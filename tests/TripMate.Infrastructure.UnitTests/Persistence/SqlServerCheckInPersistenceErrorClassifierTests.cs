using FluentAssertions;

using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class SqlServerCheckInPersistenceErrorClassifierTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(53)]
    [InlineData(10061)]
    public void ClientConnectionAndKnownInfrastructureNumbers_AreUnavailable(int number)
    {
        SqlServerCheckInPersistenceErrorClassifier.IsKnownInfrastructureFailure(number)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(18456)]
    [InlineData(2601)]
    [InlineData(2627)]
    public void AuthenticationAndConstraintNumbers_AreNotUnavailable(int number)
    {
        SqlServerCheckInPersistenceErrorClassifier.IsKnownInfrastructureFailure(number)
            .Should().BeFalse();
    }
}
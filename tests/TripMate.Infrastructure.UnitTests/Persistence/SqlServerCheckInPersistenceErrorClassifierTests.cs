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

    [Fact]
    public void TimeoutException_IsTemporarilyUnavailable()
    {
        new SqlServerCheckInPersistenceErrorClassifier()
            .IsTemporarilyUnavailable(new TimeoutException("Timeout attempting to open the connection."))
            .Should().BeTrue();
    }

    [Fact]
    public void TimeoutException_WrappedInInnerException_IsTemporarilyUnavailable()
    {
        var wrapped = new InvalidOperationException("outer", new TimeoutException("inner"));

        new SqlServerCheckInPersistenceErrorClassifier()
            .IsTemporarilyUnavailable(wrapped)
            .Should().BeTrue();
    }

    [Fact]
    public void UnrelatedException_IsNotTemporarilyUnavailable()
    {
        new SqlServerCheckInPersistenceErrorClassifier()
            .IsTemporarilyUnavailable(new InvalidOperationException("programming failure"))
            .Should().BeFalse();
    }

    [Fact]
    public void CancellationExceptions_AreNotTemporarilyUnavailable()
    {
        var classifier = new SqlServerCheckInPersistenceErrorClassifier();

        classifier.IsTemporarilyUnavailable(new OperationCanceledException()).Should().BeFalse();
        classifier.IsTemporarilyUnavailable(new TaskCanceledException()).Should().BeFalse();
    }

    [Fact]
    public void NullException_Throws()
    {
        var act = () => new SqlServerCheckInPersistenceErrorClassifier().IsTemporarilyUnavailable(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
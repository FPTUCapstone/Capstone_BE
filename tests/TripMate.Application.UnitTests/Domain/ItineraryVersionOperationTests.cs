using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class ItineraryVersionOperationTests
{
    [Fact]
    public void Create_WithValidOperation_StoresNormalizedOperationAndCompletesOnce()
    {
        var createdAt = new DateTimeOffset(2026, 9, 20, 3, 0, 0, TimeSpan.Zero);
        var operation = ItineraryVersionOperation.Create(
            42,
            100,
            ItineraryVersionOperationType.Regenerate,
            Guid.Parse("a0d84c22-26c9-4a2d-96a3-2dcf83ed7a5d"),
            "hash-1",
            createdAt);

        operation.TravelerUserId.Should().Be(42);
        operation.SourceItineraryId.Should().Be(100);
        operation.OperationType.Should().Be(ItineraryVersionOperationType.Regenerate);
        operation.RequestHash.Should().Be("hash-1");
        operation.ResultItineraryId.Should().BeNull();

        operation.Complete(101);
        operation.ResultItineraryId.Should().Be(101);

        var act = () => operation.Complete(102);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Create_RejectsEmptyKeyAndUnsupportedOperation()
    {
        var act = () => ItineraryVersionOperation.Create(
            42,
            100,
            (ItineraryVersionOperationType)999,
            Guid.Empty,
            "hash-1",
            DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }
}
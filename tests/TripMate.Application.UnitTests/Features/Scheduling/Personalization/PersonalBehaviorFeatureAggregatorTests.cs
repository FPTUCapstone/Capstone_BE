using FluentAssertions;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public sealed class PersonalBehaviorFeatureAggregatorTests
{
    [Fact]
    public async Task AggregateAsync_WithNoPoiOrCategoryIds_ReturnsEmptyWithoutDatabaseAccess()
    {
        var dbContext = new Mock<IApplicationDbContext>(MockBehavior.Strict);
        var aggregator = new PersonalBehaviorFeatureAggregator(dbContext.Object);

        PersonalBehaviorAggregation result = await aggregator.AggregateAsync(
            travelerUserId: 17L,
            candidatePoiIds: [],
            relevantCategoryIds: [],
            CancellationToken.None);

        result.PoiCounts.Should().BeEmpty();
        result.CategoryCounts.Should().BeEmpty();
        dbContext.VerifyNoOtherCalls();
    }
}
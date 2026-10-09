using FluentAssertions;

using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Application.UnitTests.Features.TripReviews;

public sealed class ReviewableRecordRefTests
{
    [Fact]
    public void EqualNumericIds_InDifferentNamespaces_AreDifferentIdentities()
    {
        var commerce = ReviewableRecordRef.CommerceBooking(123);
        var service = ReviewableRecordRef.ServiceBooking(123);

        commerce.Should().NotBe(service);
        commerce.Kind.Should().Be("commerceBooking");
        service.Kind.Should().Be("serviceBooking");
        commerce.LockKey.Should().Be("commerce-booking:123");
        service.LockKey.Should().Be("service-booking:123");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveIds_AreRejected(long id)
    {
        var commerce = () => ReviewableRecordRef.CommerceBooking(id);
        var service = () => ReviewableRecordRef.ServiceBooking(id);

        commerce.Should().Throw<ArgumentOutOfRangeException>();
        service.Should().Throw<ArgumentOutOfRangeException>();
    }
}
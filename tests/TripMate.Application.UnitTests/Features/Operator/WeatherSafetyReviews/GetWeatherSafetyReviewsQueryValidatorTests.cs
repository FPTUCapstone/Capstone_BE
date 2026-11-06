using FluentAssertions;

using TripMate.Application.Features.Operator.WeatherSafetyReviews;

namespace TripMate.Application.UnitTests.Features.Operator.WeatherSafetyReviews;

public sealed class GetWeatherSafetyReviewsQueryValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PendingAcknowledgement")]
    [InlineData("Acknowledged")]
    [InlineData("Escalated")]
    [InlineData("Closed")]
    public void KnownStatusOrNoFilter_IsValid(string? status)
    {
        new GetWeatherSafetyReviewsQueryValidator().Validate(new GetWeatherSafetyReviewsQuery(status)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("closed")]
    [InlineData("ALL")]
    public void UnknownStatus_IsRejected(string status)
    {
        var result = new GetWeatherSafetyReviewsQueryValidator().Validate(new GetWeatherSafetyReviewsQuery(status));

        result.IsValid.Should().BeFalse();
        result.Errors.Single().ErrorMessage.Should().Be("Trạng thái lọc không hợp lệ.");
    }
}

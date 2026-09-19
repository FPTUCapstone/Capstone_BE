using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using TripMate.Application.Features.Admin.SystemConfigs.Common;
using TripMate.Application.Features.Admin.SystemConfigs.GetAlgorithmParameters;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Admin.SystemConfigs.GetAlgorithmParameters;

public class GetAlgorithmParametersQueryHandlerTests
{
    private readonly TestDbContext _dbContext;
    private readonly FakeCurrentUserService _currentUserService;
    private readonly GetAlgorithmParametersQueryHandler _handler;

    public GetAlgorithmParametersQueryHandlerTests()
    {
        _dbContext = TestDbContext.Create();
        _currentUserService = new FakeCurrentUserService
        {
            UserId = 100L,
            Role = "Administrator"
        };

        _handler = new GetAlgorithmParametersQueryHandler(_dbContext, _currentUserService);
    }

    private SystemConfig SeedConfig(string key, string value, DateTimeOffset updatedAtUtc) =>
        new(key, value, description: null, updatedBy: 100L, updatedAtUtc);

    [Fact]
    public async Task Handle_WhenCallerNotAdministrator_ShouldReturnForbiddenFailure()
    {
        _currentUserService.Role = "Traveler";

        var result = await _handler.Handle(new GetAlgorithmParametersQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AlgorithmConfigErrorCodes.Forbidden);
        result.ErrorMessage.Should().Be("You do not have permission to access this function.");
    }

    [Fact]
    public async Task Handle_WhenRowsSeeded_ShouldReturnParsedValues()
    {
        var updatedAt = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        _dbContext.SystemConfigs.Add(SeedConfig("CSP.BufferTimeMinutes", "20", updatedAt));
        _dbContext.SystemConfigs.Add(SeedConfig("CSP.DefaultTravelSpeedKmh", "35.5", updatedAt));
        _dbContext.SystemConfigs.Add(SeedConfig("Rerouting.SearchRadiusKm", "10", updatedAt));
        _dbContext.SystemConfigs.Add(SeedConfig("Weather.AlertThresholdSeverity", "Extreme", updatedAt));
        _dbContext.SaveChanges();

        var result = await _handler.Handle(new GetAlgorithmParametersQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BufferTimeMinutes.Should().Be(20);
        result.Value.DefaultTravelSpeedKmh.Should().Be(35.5);
        result.Value.ReroutingSearchRadiusKm.Should().Be(10);
        result.Value.WeatherAlertThresholdSeverity.Should().Be("Extreme");
        result.Value.UpdatedAtUtc.Should().Be(updatedAt);
        result.Value.UpdatedAtLocal.Should().Be("16/09/2026 19:00:00");
    }

    [Fact]
    public async Task Handle_WhenRowsMissing_ShouldReturnSeededDefaults()
    {
        var result = await _handler.Handle(new GetAlgorithmParametersQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BufferTimeMinutes.Should().Be(15);
        result.Value.DefaultTravelSpeedKmh.Should().Be(30);
        result.Value.ReroutingSearchRadiusKm.Should().Be(5);
        result.Value.WeatherAlertThresholdSeverity.Should().Be("Severe");
    }

    [Fact]
    public async Task Handle_WhenForeignConfigRowsExist_ShouldIgnoreThem()
    {
        var updatedAt = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        _dbContext.SystemConfigs.Add(SeedConfig("Booking.PaymentExpiryMinutes", "99", updatedAt));
        _dbContext.SaveChanges();

        var result = await _handler.Handle(new GetAlgorithmParametersQuery(), CancellationToken.None);

        result.Value.BufferTimeMinutes.Should().Be(15);
    }

    [Fact]
    public async Task Handle_WhenPartialRows_ShouldUseLatestUpdatedAtForPresentRows()
    {
        var latest = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        _dbContext.SystemConfigs.Add(SeedConfig("CSP.BufferTimeMinutes", "25", latest));
        _dbContext.SystemConfigs.Add(SeedConfig("CSP.DefaultTravelSpeedKmh", "40", latest.AddHours(-2)));
        _dbContext.SaveChanges();

        var result = await _handler.Handle(new GetAlgorithmParametersQuery(), CancellationToken.None);

        result.Value.UpdatedAtUtc.Should().Be(latest);
    }
}
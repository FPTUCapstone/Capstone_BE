using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using TripMate.Application.Features.Admin.SystemConfigs.Common;
using TripMate.Application.Features.Admin.SystemConfigs.UpdateAlgorithmParameters;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Admin.SystemConfigs.UpdateAlgorithmParameters;

public class UpdateAlgorithmParametersCommandHandlerTests
{
    private static readonly DateTimeOffset Clock = new(2026, 9, 19, 4, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext _dbContext;
    private readonly FakeCurrentUserService _currentUserService;
    private readonly FakeDateTimeProvider _dateTimeProvider;
    private readonly UpdateAlgorithmParametersCommandHandler _handler;

    public UpdateAlgorithmParametersCommandHandlerTests()
    {
        _dbContext = TestDbContext.Create();
        _currentUserService = new FakeCurrentUserService
        {
            UserId = 100L,
            Role = "Administrator"
        };
        _dateTimeProvider = new FakeDateTimeProvider { UtcNow = Clock };

        _handler = new UpdateAlgorithmParametersCommandHandler(
            _dbContext, _currentUserService, _dateTimeProvider);
    }

    private SystemConfig SeedConfig(string key, string value) =>
        new(key, value, description: null, updatedBy: 1L, Clock.AddHours(-24));

    private static UpdateAlgorithmParametersCommand ValidCommand(
        int buffer = 20, double speed = 35, double radius = 10, string severity = "Severe") =>
        new(buffer, speed, radius, severity);

    [Fact]
    public async Task Handle_WhenCallerNotAdministrator_ShouldReturnForbiddenAndChangeNothing()
    {
        _dbContext.SystemConfigs.Add(SeedConfig("CSP.BufferTimeMinutes", "15"));
        _dbContext.SaveChanges();
        _currentUserService.Role = "Traveler";

        var result = await _handler.Handle(ValidCommand(), CancellationToken.None);

        result.ErrorCode.Should().Be(AlgorithmConfigErrorCodes.Forbidden);
        result.ErrorMessage.Should().Be("You do not have permission to access this function.");
        _dbContext.SystemConfigs.Single(c => c.ConfigKey == "CSP.BufferTimeMinutes").ConfigValue.Should().Be("15");
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenValid_ShouldUpsertRowsAndWriteSuccessAuditEntry()
    {
        _dbContext.SystemConfigs.Add(SeedConfig("CSP.BufferTimeMinutes", "15"));
        _dbContext.SystemConfigs.Add(SeedConfig("Weather.AlertThresholdSeverity", "Moderate"));
        _dbContext.SaveChanges();

        var result = await _handler.Handle(ValidCommand(severity: "extreme"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BufferTimeMinutes.Should().Be(20);
        result.Value.WeatherAlertThresholdSeverity.Should().Be("Extreme");
        result.Value.UpdatedAtUtc.Should().Be(Clock);
        result.Value.UpdatedAtLocal.Should().Be("19/09/2026 11:00:00");

        var rows = _dbContext.SystemConfigs.ToList();
        rows.Single(c => c.ConfigKey == "CSP.BufferTimeMinutes").ConfigValue.Should().Be("20");
        rows.Single(c => c.ConfigKey == "CSP.BufferTimeMinutes").UpdatedBy.Should().Be(100L);
        rows.Single(c => c.ConfigKey == "CSP.BufferTimeMinutes").UpdatedAtUtc.Should().Be(Clock);
        rows.Single(c => c.ConfigKey == "CSP.DefaultTravelSpeedKmh").ConfigValue.Should().Be("35");
        rows.Single(c => c.ConfigKey == "Rerouting.SearchRadiusKm").ConfigValue.Should().Be("10");
        rows.Single(c => c.ConfigKey == "Weather.AlertThresholdSeverity").ConfigValue.Should().Be("Extreme");

        var audit = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        audit.ActionType.Should().Be(AuditActionTypes.AlgorithmParametersUpdate);
        audit.AffectedEntity.Should().Be(AuditEntityTypes.SystemConfig);
        audit.ActorUserId.Should().Be(100L);
        audit.Result.Should().Be(AuditOutcome.Success);
        audit.CreatedAtUtc.Should().Be(Clock);
        audit.BeforeData.Should().Contain("CSP.BufferTimeMinutes").And.Contain("\"15\"");
        audit.AfterData.Should().Contain("CSP.BufferTimeMinutes").And.Contain("\"20\"");
    }

    [Fact]
    public async Task Handle_WhenForeignConfigRowExists_ShouldLeaveItUntouched()
    {
        _dbContext.SystemConfigs.Add(SeedConfig("Booking.PaymentExpiryMinutes", "15"));
        _dbContext.SaveChanges();

        var result = await _handler.Handle(ValidCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _dbContext.SystemConfigs.Single(c => c.ConfigKey == "Booking.PaymentExpiryMinutes").ConfigValue.Should().Be("15");
    }

    [Theory]
    [InlineData(4, 35, 10, "Severe")]
    [InlineData(61, 35, 10, "Severe")]
    [InlineData(20, 9, 10, "Severe")]
    [InlineData(20, 121, 10, "Severe")]
    [InlineData(20, 35, 0, "Severe")]
    [InlineData(20, 35, 51, "Severe")]
    [InlineData(20, double.NaN, 10, "Severe")]
    [InlineData(20, 35, double.NaN, "Severe")]
    [InlineData(20, double.PositiveInfinity, 10, "Severe")]
    [InlineData(20, 35, double.NegativeInfinity, "Severe")]
    [InlineData(20, 35, 10, "")]
    [InlineData(20, 35, 10, "Catastrophic")]
    public async Task Handle_WhenValueOutOfRange_ShouldReturn422AndChangeNothing(
        int buffer, double speed, double radius, string severity)
    {
        _dbContext.SystemConfigs.Add(SeedConfig("CSP.BufferTimeMinutes", "15"));
        _dbContext.SaveChanges();

        var result = await _handler.Handle(ValidCommand(buffer, speed, radius, severity), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AlgorithmConfigErrorCodes.InvalidValue);
        result.ErrorMessage.Should().Be("Parameter value out of allowed range (e.g., buffer time must be 5-60 mins).");
        _dbContext.SystemConfigs.Single(c => c.ConfigKey == "CSP.BufferTimeMinutes").ConfigValue.Should().Be("15");
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenBoundaryValues_ShouldSucceed()
    {
        var result = await _handler.Handle(ValidCommand(5, 10, 1, "Moderate"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var second = await _handler.Handle(ValidCommand(60, 120, 50, "Extreme"), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
    }
}
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using TripMate.Application.Features.Admin.AuditLogs.Common;
using TripMate.Application.Features.Admin.AuditLogs.GetDetail;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Admin.AuditLogs.GetDetail;

public class GetAuditLogDetailQueryHandlerTests
{
    private readonly TestDbContext _dbContext;
    private readonly FakeCurrentUserService _currentUserService;
    private readonly GetAuditLogDetailQueryHandler _handler;

    public GetAuditLogDetailQueryHandlerTests()
    {
        _dbContext = TestDbContext.Create();
        _currentUserService = new FakeCurrentUserService
        {
            UserId = 100L,
            Role = "Administrator"
        };

        _handler = new GetAuditLogDetailQueryHandler(_dbContext, _currentUserService);
    }

    [Fact]
    public async Task Handle_WhenCallerNotAdministrator_ShouldReturnForbiddenFailure()
    {
        // Arrange
        _currentUserService.Role = "Traveler";
        var query = new GetAuditLogDetailQuery(1L);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AuditLogErrorCodes.Forbidden);
        result.ErrorMessage.Should().Be("Access denied. Administrator role required.");
    }

    [Fact]
    public async Task Handle_WhenAuditLogNotFound_ShouldReturnNotFoundFailure()
    {
        // Arrange
        var query = new GetAuditLogDetailQuery(999L);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AuditLogErrorCodes.NotFound);
        result.ErrorMessage.Should().Be("System audit log entry not found.");
    }

    [Fact]
    public async Task Handle_WhenAuditLogExists_ShouldReturnLogDetailWithLocalTime()
    {
        // Arrange
        var adminUser = new User
        {
            Id = 10L,
            FullName = "Admin User",
            Email = "admin@tripmate.vn",
            Role = UserRole.Administrator,
            Status = AccountStatus.Active
        };
        _dbContext.Users.Add(adminUser);

        var utcTime = new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        var auditLog = new AuditLog
        {
            Id = 101L,
            ActorUserId = adminUser.Id,
            ActorUser = adminUser,
            ActionType = "ApproveOperatorApplication",
            AffectedEntity = "OperatorProfile",
            AffectedEntityId = 5L,
            BeforeData = "{\"status\":\"Pending\"}",
            AfterData = "{\"status\":\"Approved\"}",
            IpAddress = "127.0.0.1",
            CreatedAtUtc = utcTime
        };
        _dbContext.AuditLogs.Add(auditLog);
        _dbContext.SaveChanges();

        var query = new GetAuditLogDetailQuery(101L);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.Id.Should().Be(101L);
        result.Value.ActionType.Should().Be("ApproveOperatorApplication");
        result.Value.ActorUserId.Should().Be(10L);
        result.Value.ActorEmail.Should().Be("admin@tripmate.vn");
        result.Value.ActorFullName.Should().Be("Admin User");
        result.Value.ActorRole.Should().Be(UserRole.Administrator);
        result.Value.BeforeData.Should().Be("{\"status\":\"Pending\"}");
        result.Value.AfterData.Should().Be("{\"status\":\"Approved\"}");
        result.Value.IpAddress.Should().Be("127.0.0.1");
        result.Value.CreatedAtUtc.Should().Be(utcTime);
        result.Value.CreatedAtLocal.Should().Be("13/09/2026 17:00:00");
    }

    [Fact]
    public async Task Handle_WhenSystemActor_ShouldReturnSystemAsActorFullName()
    {
        // Arrange
        var utcTime = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var systemLog = new AuditLog
        {
            Id = 202L,
            ActorUserId = null,
            ActorUser = null,
            ActionType = "SystemAutoSync",
            AffectedEntity = "TourDeparture",
            AffectedEntityId = 88L,
            BeforeData = null,
            AfterData = null,
            IpAddress = null,
            CreatedAtUtc = utcTime
        };
        _dbContext.AuditLogs.Add(systemLog);
        _dbContext.SaveChanges();

        var query = new GetAuditLogDetailQuery(202L);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value.Id.Should().Be(202L);
        result.Value.ActorUserId.Should().BeNull();
        result.Value.ActorEmail.Should().BeNull();
        result.Value.ActorFullName.Should().Be("System");
        result.Value.ActorRole.Should().BeNull();
    }
}

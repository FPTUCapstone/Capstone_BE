using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using TripMate.Application.Features.Admin.AuditLogs.Common;
using TripMate.Application.Features.Admin.AuditLogs.GetList;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Admin.AuditLogs.GetList;

public class GetAuditLogsQueryHandlerTests
{
    private readonly TestDbContext _dbContext;
    private readonly FakeCurrentUserService _currentUserService;
    private readonly GetAuditLogsQueryHandler _handler;

    public GetAuditLogsQueryHandlerTests()
    {
        _dbContext = TestDbContext.Create();
        _currentUserService = new FakeCurrentUserService
        {
            UserId = 100L,
            Role = "Administrator"
        };

        _handler = new GetAuditLogsQueryHandler(_dbContext, _currentUserService);
    }

    [Fact]
    public async Task Handle_WhenCallerNotAdministrator_ShouldReturnForbiddenFailure()
    {
        // Arrange
        _currentUserService.Role = "Traveler";
        var query = new GetAuditLogsQuery();

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AuditLogErrorCodes.Forbidden);
        result.ErrorMessage.Should().Be("Access denied. Administrator role required.");
    }

    [Fact]
    public async Task Handle_WhenInvalidDateRange_ShouldReturnInvalidDateRangeFailure()
    {
        // Arrange
        var fromDate = DateTimeOffset.UtcNow;
        var toDate = fromDate.AddDays(-1); // Invalid: FromDate > ToDate
        var query = new GetAuditLogsQuery(FromDateUtc: fromDate, ToDateUtc: toDate);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AuditLogErrorCodes.InvalidDateRange);
        result.ErrorMessage.Should().Be("The submitted Event Date range is logically invalid.");
    }

    [Fact]
    public async Task Handle_WhenNoFiltersProvided_ShouldReturnRecentLogsOrderedDescending()
    {
        // Arrange
        var adminUser = SeedUser(1, "Admin User", "admin@tripmate.vn", UserRole.Administrator);

        var baseTime = DateTimeOffset.UtcNow.AddHours(-10);
        SeedAuditLog(adminUser, "Action1", "OperatorProfile", 1, baseTime);
        SeedAuditLog(adminUser, "Action2", "OperatorProfile", 2, baseTime.AddHours(2));
        SeedAuditLog(adminUser, "Action3", "OperatorProfile", 3, baseTime.AddHours(5));

        var query = new GetAuditLogsQuery();

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(3);
        result.Value.Items.Should().HaveCount(3);
        result.Value.Items[0].ActionType.Should().Be("Action3");
        result.Value.Items[1].ActionType.Should().Be("Action2");
        result.Value.Items[2].ActionType.Should().Be("Action1");
    }

    [Fact]
    public async Task Handle_WhenFilteredByActionType_ShouldReturnOnlyMatchingLogs()
    {
        // Arrange
        var adminUser = SeedUser(1, "Admin User", "admin@tripmate.vn", UserRole.Administrator);
        SeedAuditLog(adminUser, "ApproveOperatorApplication", "OperatorProfile", 10, DateTimeOffset.UtcNow);
        SeedAuditLog(adminUser, "RejectOperatorApplication", "OperatorProfile", 20, DateTimeOffset.UtcNow);

        var query = new GetAuditLogsQuery(ActionType: "ApproveOperatorApplication");

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().ActionType.Should().Be("ApproveOperatorApplication");
    }

    [Fact]
    public async Task Handle_WhenFilteredByActorRole_ShouldReturnOnlyMatchingLogs()
    {
        // Arrange
        var adminUser = SeedUser(1, "Admin User", "admin@tripmate.vn", UserRole.Administrator);
        var operatorUser = SeedUser(2, "Operator User", "operator@tripmate.vn", UserRole.TourOperator);

        SeedAuditLog(adminUser, "AdminAction", "OperatorProfile", 1, DateTimeOffset.UtcNow);
        SeedAuditLog(operatorUser, "OperatorAction", "TourPackage", 2, DateTimeOffset.UtcNow);

        var query = new GetAuditLogsQuery(ActorRole: UserRole.TourOperator);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().ActorRole.Should().Be(UserRole.TourOperator);
    }

    [Fact]
    public async Task Handle_WhenKeywordIsNumericId_ShouldSearchAffectedEntityIdWithoutException()
    {
        // Arrange
        var adminUser = SeedUser(1, "Admin User", "admin@tripmate.vn", UserRole.Administrator);
        SeedAuditLog(adminUser, "ActionA", "OperatorProfile", 42L, DateTimeOffset.UtcNow);
        SeedAuditLog(adminUser, "ActionB", "OperatorProfile", 99L, DateTimeOffset.UtcNow);

        var query = new GetAuditLogsQuery(Keyword: "42");

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Single().AffectedEntityId.Should().Be(42L);
    }

    [Fact]
    public async Task Handle_WhenSystemTriggeredLogHasNullActor_ShouldHandleNullActorSafelyAndDisplaySystem()
    {
        // Arrange
        var log = new AuditLog
        {
            ActorUserId = null,
            ActorUser = null,
            ActionType = "HandleEmergencyCancellation",
            AffectedEntity = "TourDeparture",
            AffectedEntityId = 555L,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _dbContext.AuditLogs.Add(log);
        _dbContext.SaveChanges();

        var query = new GetAuditLogsQuery();

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        var item = result.Value.Items.Single();
        item.ActorUserId.Should().BeNull();
        item.ActorEmail.Should().BeNull();
        item.ActorFullName.Should().Be("System");
        item.ActorRole.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenPaginationRequested_ShouldReturnCorrectPageAndMetadata()
    {
        // Arrange
        var adminUser = SeedUser(1, "Admin User", "admin@tripmate.vn", UserRole.Administrator);
        var now = DateTimeOffset.UtcNow;
        for (int i = 1; i <= 25; i++)
        {
            SeedAuditLog(adminUser, $"Action_{i:D2}", "Entity", i, now.AddMinutes(i));
        }

        var query = new GetAuditLogsQuery(PageNumber: 2, PageSize: 10);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(25);
        result.Value.PageNumber.Should().Be(2);
        result.Value.PageSize.Should().Be(10);
        result.Value.TotalPages.Should().Be(3);
        result.Value.HasPreviousPage.Should().BeTrue();
        result.Value.HasNextPage.Should().BeTrue();
        result.Value.Items.Should().HaveCount(10);
    }

    private User SeedUser(long id, string name, string email, UserRole role)
    {
        var user = new User
        {
            Id = id,
            FullName = name,
            Email = email,
            Role = role,
            Status = AccountStatus.Active,
        };
        _dbContext.Users.Add(user);
        _dbContext.SaveChanges();
        return user;
    }

    private void SeedAuditLog(User user, string actionType, string affectedEntity, long entityId, DateTimeOffset createdAt)
    {
        var log = new AuditLog
        {
            ActorUserId = user.Id,
            ActorUser = user,
            ActionType = actionType,
            AffectedEntity = affectedEntity,
            AffectedEntityId = entityId,
            CreatedAtUtc = createdAt,
        };
        _dbContext.AuditLogs.Add(log);
        _dbContext.SaveChanges();
    }
}

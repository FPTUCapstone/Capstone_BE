using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Features.Admin.TourOperatorApplications.Approve;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Admin.TourOperatorApplications.Approve;

public class ApproveOperatorApplicationCommandHandlerTests
{
    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly FakeCurrentUserService _currentUserService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly ApproveOperatorApplicationCommandHandler _handler;

    public ApproveOperatorApplicationCommandHandlerTests()
    {
        _currentUserService.UserId = 100;
        _currentUserService.Role = "Administrator";
        _handler = new ApproveOperatorApplicationCommandHandler(_dbContext, _currentUserService, _dateTimeProvider);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotAdmin_ShouldReturnForbiddenFailure()
    {
        // Arrange
        _currentUserService.Role = "Traveler";
        var command = new ApproveOperatorApplicationCommand(1);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_WhenUserDoesNotExist_ShouldReturnNotFoundFailure()
    {
        // Arrange
        var command = new ApproveOperatorApplicationCommand(999);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_WhenUserIsNotTourOperator_ShouldReturnWrongRoleFailure()
    {
        // Arrange
        var user = new User
        {
            FullName = "Traveler User",
            Email = "traveler@example.com",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var command = new ApproveOperatorApplicationCommand(user.Id);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.WrongRole);
    }

    [Fact]
    public async Task Handle_WhenAccountNotPendingApproval_ShouldReturnNotPendingFailure()
    {
        // Arrange
        var user = new User
        {
            FullName = "Active Operator",
            Email = "active@example.com",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var command = new ApproveOperatorApplicationCommand(user.Id);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.NotPending);
    }

    [Fact]
    public async Task Handle_WhenProfileIncomplete_ShouldReturnIncompleteFailure()
    {
        // Arrange
        var user = new User
        {
            FullName = "Incomplete Operator",
            Email = "incomplete@example.com",
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingApproval,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var profile = new OperatorProfile
        {
            User = user,
            CompanyName = "",
            TaxCode = "TAX-123",
            BusinessLicenseNo = "LIC-123",
            ApprovalStatus = OperatorApprovalStatus.PendingApproval,
        };
        _dbContext.OperatorProfiles.Add(profile);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var command = new ApproveOperatorApplicationCommand(user.Id);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.Incomplete);
    }

    [Fact]
    public async Task Handle_WhenMissingBusinessLicenseDocument_ShouldReturnDocumentInvalidFailure()
    {
        // Arrange: no documents added — BusinessLicense is the only mandatory document.
        // A separate TaxCode document upload is not required per SRS §3.2.2.
        var user = SeedPendingOperatorUser();
        SeedPendingOperatorProfile(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var command = new ApproveOperatorApplicationCommand(user.Id);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.DocumentInvalid);
    }

    [Fact]
    public async Task Handle_WhenBusinessLicenseDocumentIsRejected_ShouldReturnDocumentInvalidFailure()
    {
        // Arrange: BusinessLicense present but Rejected — should block approval.
        var user = SeedPendingOperatorUser();
        var profile = SeedPendingOperatorProfile(user);

        _dbContext.OperatorDocuments.Add(new OperatorDocument
        {
            OperatorProfile = profile,
            DocumentType = OperatorDocumentType.BusinessLicense,
            FileUrl = "https://storage.tripmate.vn/license.pdf",
            Status = DocumentStatus.Rejected,
        });
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var command = new ApproveOperatorApplicationCommand(user.Id);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.DocumentInvalid);
    }

    [Fact]
    public async Task Handle_WhenValidPendingApplication_ShouldApproveAndCreateAuditLogAndNotification()
    {
        // Arrange
        var user = SeedPendingOperatorUser();
        var profile = SeedPendingOperatorProfile(user);

        // Only BusinessLicense is required; TaxCode document upload is not mandatory.
        var doc1 = new OperatorDocument
        {
            OperatorProfile = profile,
            DocumentType = OperatorDocumentType.BusinessLicense,
            FileUrl = "https://storage.tripmate.vn/license.pdf",
            Status = DocumentStatus.Submitted,
        };

        _dbContext.OperatorDocuments.Add(doc1);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var command = new ApproveOperatorApplicationCommand(user.Id);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(user.Id);
        result.Value.AccountStatus.Should().Be(AccountStatus.Active);
        result.Value.ApplicationStatus.Should().Be(OperatorApprovalStatus.Approved);
        result.Value.ReviewedBy.Should().Be(100);
        result.Value.ReviewedAt.Should().Be(_dateTimeProvider.UtcNow);
        result.Value.Message.Should().Contain("Viet Travel Co");

        // Verify Database Changes
        var updatedUser = await _dbContext.Users.FirstAsync(u => u.Id == user.Id);
        updatedUser.Status.Should().Be(AccountStatus.Active);
        updatedUser.Role.Should().Be(UserRole.TourOperator);

        var updatedProfile = await _dbContext.OperatorProfiles.Include(p => p.Documents).FirstAsync(p => p.UserId == user.Id);
        updatedProfile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Approved);
        updatedProfile.ReviewedBy.Should().Be(100);
        updatedProfile.ReviewedAtUtc.Should().Be(_dateTimeProvider.UtcNow);
        updatedProfile.RejectionReason.Should().BeNull();
        updatedProfile.Documents.All(d => d.Status == DocumentStatus.Approved).Should().BeTrue();

        // Verify AuditLog inserted
        var auditLog = await _dbContext.AuditLogs.FirstOrDefaultAsync(a => a.AffectedEntityId == user.Id);
        auditLog.Should().NotBeNull();
        auditLog!.ActionType.Should().Be("ApproveOperatorApplication");
        auditLog.AffectedEntity.Should().Be("OperatorProfile");
        auditLog.ActorUserId.Should().Be(100);
        auditLog.BeforeData.Should().Contain("PendingApproval");
        auditLog.AfterData.Should().Contain("Approved");

        // Verify Notification inserted
        var notification = await _dbContext.Notifications.FirstOrDefaultAsync(n => n.UserId == user.Id);
        notification.Should().NotBeNull();
        notification!.Channel.Should().Be(NotificationChannel.Email);
        notification.Type.Should().Be("OperatorApplicationApproved");
        notification.Status.Should().Be(NotificationStatus.Pending);
        notification.Body.Should().Contain("Viet Travel Co");
    }

    private User SeedPendingOperatorUser()
    {
        var user = new User
        {
            FullName = "Viet Travel Admin",
            Email = "contact@viettravel.com",
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingApproval,
        };
        _dbContext.Users.Add(user);
        return user;
    }

    private OperatorProfile SeedPendingOperatorProfile(User user)
    {
        var profile = new OperatorProfile
        {
            User = user,
            CompanyName = "Viet Travel Co",
            TaxCode = "TAX-999000",
            BusinessLicenseNo = "LIC-999000",
            ApprovalStatus = OperatorApprovalStatus.PendingApproval,
        };
        _dbContext.OperatorProfiles.Add(profile);
        return profile;
    }
}

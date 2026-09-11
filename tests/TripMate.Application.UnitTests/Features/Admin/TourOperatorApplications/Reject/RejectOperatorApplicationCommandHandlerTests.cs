using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;
using TripMate.Application.Features.Admin.TourOperatorApplications.Reject;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Admin.TourOperatorApplications.Reject;

public class RejectOperatorApplicationCommandHandlerTests
{
    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly FakeCurrentUserService _currentUserService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly RejectOperatorApplicationCommandHandler _handler;

    public RejectOperatorApplicationCommandHandlerTests()
    {
        _currentUserService.UserId = 100;
        _currentUserService.Role = "Administrator";
        _handler = new RejectOperatorApplicationCommandHandler(_dbContext, _currentUserService, _dateTimeProvider);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotAdmin_ShouldReturnForbiddenFailure()
    {
        // Arrange
        _currentUserService.Role = "Traveler";
        var command = new RejectOperatorApplicationCommand(1, "Invalid documents");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_WhenRejectionReasonIsEmpty_ShouldReturnRejectionReasonRequiredFailure()
    {
        // Arrange
        var command = new RejectOperatorApplicationCommand(1, "   ");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.RejectionReasonRequired);
    }

    [Fact]
    public async Task Handle_WhenRejectionReasonTooLong_ShouldReturnRejectionReasonTooLongFailure()
    {
        // Arrange
        var longReason = new string('A', 1001);
        var command = new RejectOperatorApplicationCommand(1, longReason);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.RejectionReasonTooLong);
    }

    [Fact]
    public async Task Handle_WhenUserDoesNotExist_ShouldReturnNotFoundFailure()
    {
        // Arrange
        var command = new RejectOperatorApplicationCommand(999, "Business license invalid");

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

        var command = new RejectOperatorApplicationCommand(user.Id, "Invalid request");

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

        var command = new RejectOperatorApplicationCommand(user.Id, "Invalid documents");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.NotPending);
    }

    [Fact]
    public async Task Handle_WhenValidPendingApplication_ShouldRejectAndCreateAuditLogAndNotification()
    {
        // Arrange
        var user = SeedPendingOperatorUser();
        var profile = SeedPendingOperatorProfile(user);

        var doc1 = new OperatorDocument
        {
            OperatorProfile = profile,
            DocumentType = OperatorDocumentType.BusinessLicense,
            FileUrl = "https://storage.tripmate.vn/license.pdf",
            Status = DocumentStatus.Submitted,
        };

        _dbContext.OperatorDocuments.Add(doc1);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        const string rejectionReason = "Business license registration number could not be verified with tax authorities.";
        var command = new RejectOperatorApplicationCommand(user.Id, rejectionReason);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(user.Id);
        result.Value.AccountStatus.Should().Be(AccountStatus.Rejected);
        result.Value.ApplicationStatus.Should().Be(OperatorApprovalStatus.Rejected);
        result.Value.RejectionReason.Should().Be(rejectionReason);
        result.Value.ReviewedBy.Should().Be(100);
        result.Value.ReviewedAt.Should().Be(_dateTimeProvider.UtcNow);
        result.Value.Message.Should().Be("Application rejected. Notification sent to operator.");

        // Verify Database Changes
        var updatedUser = await _dbContext.Users.FirstAsync(u => u.Id == user.Id);
        updatedUser.Status.Should().Be(AccountStatus.Rejected);

        var updatedProfile = await _dbContext.OperatorProfiles.Include(p => p.Documents).FirstAsync(p => p.UserId == user.Id);
        updatedProfile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Rejected);
        updatedProfile.RejectionReason.Should().Be(rejectionReason);
        updatedProfile.ReviewedBy.Should().Be(100);
        updatedProfile.ReviewedAtUtc.Should().Be(_dateTimeProvider.UtcNow);
        updatedProfile.Documents.All(d => d.Status == DocumentStatus.Rejected).Should().BeTrue();

        // Verify AuditLog inserted
        var auditLog = await _dbContext.AuditLogs.FirstOrDefaultAsync(a => a.AffectedEntityId == user.Id);
        auditLog.Should().NotBeNull();
        auditLog!.ActionType.Should().Be("RejectOperatorApplication");
        auditLog.AffectedEntity.Should().Be("OperatorProfile");
        auditLog.ActorUserId.Should().Be(100);
        auditLog.BeforeData.Should().Contain("PendingApproval");
        auditLog.AfterData.Should().Contain("Rejected");
        auditLog.AfterData.Should().Contain(rejectionReason);

        // Verify Notification inserted
        var notification = await _dbContext.Notifications.FirstOrDefaultAsync(n => n.UserId == user.Id);
        notification.Should().NotBeNull();
        notification!.Channel.Should().Be(NotificationChannel.Email);
        notification.Type.Should().Be("OperatorApplicationRejected");
        notification.Status.Should().Be(NotificationStatus.Pending);
        notification.Body.Should().Contain("Viet Travel Co");
        notification.Body.Should().Contain(rejectionReason);
    }

    [Fact]
    public async Task Handle_WhenConcurrentRejectionConflictOccurs_ShouldReturnNotPendingFailure()
    {
        // Arrange
        var user = SeedPendingOperatorUser();
        SeedPendingOperatorProfile(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Simulate concurrent modification where another admin rejected/approved first
        _dbContext.ThrowOnSaveConcurrency = true;

        var command = new RejectOperatorApplicationCommand(user.Id, "Invalid documents");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.NotPending);
    }

    [Fact]
    public async Task Handle_WhenMessageCatalogConfiguredInDatabase_ShouldResolveRejectTemplateFromDatabase()
    {
        // Arrange
        var user = SeedPendingOperatorUser();
        SeedPendingOperatorProfile(user);

        _dbContext.Messages.Add(new Message
        {
            MessageCode = "MSG116",
            MessageType = "ToastMessage",
            ContentTemplate = "Hồ sơ đã bị từ chối và thông báo đã được gửi.",
            CreatedAtUtc = _dateTimeProvider.UtcNow,
        });
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var command = new RejectOperatorApplicationCommand(user.Id, "Giấy phép không hợp lệ");

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Message.Should().Be("Hồ sơ đã bị từ chối và thông báo đã được gửi.");
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

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.Authentication.RegisterOperator;
using TripMate.Application.Features.Operator.Application.Common;
using TripMate.Application.Features.Operator.Application.Resubmit;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Operator.Application;

public sealed class ResubmitOperatorApplicationCommandHandlerTests
{
    private readonly TestDbContext db = TestDbContext.Create();
    private readonly Mock<ICurrentUserService> currentUser = new();
    private readonly Mock<IDateTimeProvider> clock = new();
    private readonly Mock<IOperatorDocumentStorage> storage = new();
    private readonly Mock<IOperatorDocumentCleanupJournal> cleanup = new();
    private readonly Mock<IOperatorRegistrationConstraintClassifier> constraints = new();
    private readonly DateTimeOffset now = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly List<string> deleted = [];
    private int uploadCount;
    private const long CurrentUserId = 42;

    public ResubmitOperatorApplicationCommandHandlerTests()
    {
        clock.Setup(x => x.UtcNow).Returns(now);
        currentUser.Setup(x => x.UserId).Returns(CurrentUserId);
        currentUser.Setup(x => x.Role).Returns("TourOperator");

        storage.Setup(x => x.AllocatePublicId()).Returns(() => $"operators/resubmit-{++uploadCount}");
        storage.Setup(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperatorDocumentStorageUpload upload, CancellationToken _) =>
                OperatorDocumentStorageUploadResult.Succeeded(new Uri($"https://cdn.example.com/{upload.PublicId}")));
        storage.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string _, CancellationToken _) =>
            {
                deleted.Add(id);
                return new OperatorDocumentStorageDeleteResult(OperatorDocumentStorageDeleteOutcome.Deleted, null);
            });
    }

    private ResubmitOperatorApplicationCommandHandler Handler() => new(
        db,
        currentUser.Object,
        clock.Object,
        storage.Object,
        cleanup.Object,
        constraints.Object,
        NullLogger<ResubmitOperatorApplicationCommandHandler>.Instance);

    private static ResubmitOperatorApplicationCommand Command(
        OperatorRegistrationDocument? license = null,
        IReadOnlyList<OperatorRegistrationDocument>? supporting = null,
        string taxCode = "0101234567",
        string licenseNo = "79-0123/2026/TCDL-GPLHQT") => new(
        "Updated Company",
        licenseNo,
        taxCode,
        "Updated Contact",
        "Updated Address",
        "0987654321",
        license,
        supporting);

    private (User User, OperatorProfile Profile) SeedRejectedApplication(
        string taxCode = "0101234567",
        string licenseNo = "79-0123/2026/TCDL-GPLHQT",
        bool includeLicenseDoc = true)
    {
        var user = new User
        {
            Id = CurrentUserId,
            Email = "operator@example.com",
            FullName = "Original Contact",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Rejected,
        };
        db.Users.Add(user);

        var profile = new OperatorProfile
        {
            UserId = CurrentUserId,
            User = user,
            CompanyName = "Original Company",
            TaxCode = taxCode,
            BusinessLicenseNo = licenseNo,
            ContactAddress = "Original Address",
            ContactPhone = "0900000000",
            ApprovalStatus = OperatorApprovalStatus.Rejected,
            RejectionReason = "Unreadable license scan",
            ReviewedBy = 999,
            ReviewedAtUtc = now.AddDays(-2),
            UpdatedAtUtc = now.AddDays(-2),
        };

        if (includeLicenseDoc)
        {
            profile.Documents.Add(new OperatorDocument
            {
                Id = 101,
                OperatorUserId = CurrentUserId,
                DocumentType = OperatorDocumentType.BusinessLicense,
                FileUrl = "https://cdn.example.com/operators/old-license.pdf",
                Status = DocumentStatus.Rejected,
                UploadedAtUtc = now.AddDays(-5),
            });
        }

        db.OperatorProfiles.Add(profile);
        db.SaveChanges();
        return (user, profile);
    }

    [Fact]
    public async Task Handle_WhenNotTourOperator_ReturnsForbidden()
    {
        currentUser.Setup(x => x.Role).Returns("Traveler");
        var result = await Handler().Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorApplicationErrorCodes.Forbidden);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenProfileMissing_ReturnsNotFound()
    {
        var result = await Handler().Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorApplicationErrorCodes.NotFound);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.PendingApproval)]
    [InlineData(AccountStatus.Active, OperatorApprovalStatus.Approved)]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.PendingApproval)]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.Rejected)]
    public async Task Handle_WhenNotRejected_ReturnsNotRejectedMsg161(
        AccountStatus userStatus, OperatorApprovalStatus approvalStatus)
    {
        var (user, profile) = SeedRejectedApplication();
        user.Status = userStatus;
        profile.ApprovalStatus = approvalStatus;
        await db.SaveChangesAsync();

        var result = await Handler().Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorApplicationErrorCodes.NotRejected);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenNoNewLicenseAndNoRetainedLicense_ReturnsMissingLicenseMsg157()
    {
        SeedRejectedApplication(includeLicenseDoc: false);

        var result = await Handler().Handle(Command(license: null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorApplicationErrorCodes.MissingBusinessLicense);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_WithNewBusinessLicense_UpdatesStatusClearsMetadataAndAudits()
    {
        SeedRejectedApplication();

        var newLicenseDoc = new OperatorRegistrationDocument("new-license.pdf", "application/pdf", "%PDF-new"u8.ToArray());
        var supportingDoc = new OperatorRegistrationDocument("cert.png", "image/png", [137, 80, 78, 71, 13, 10, 26, 10]);

        var result = await Handler().Handle(
            Command(license: newLicenseDoc, supporting: [supportingDoc]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserStatus.Should().Be(nameof(AccountStatus.PendingApproval));
        result.Value.ApprovalStatus.Should().Be(nameof(OperatorApprovalStatus.PendingApproval));
        result.Value.MessageCode.Should().Be("MSG162");

        var user = await db.Users.SingleAsync(u => u.Id == CurrentUserId);
        user.Status.Should().Be(AccountStatus.PendingApproval);
        user.FullName.Should().Be("Updated Contact");

        var profile = await db.OperatorProfiles.Include(p => p.Documents).SingleAsync(p => p.UserId == CurrentUserId);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.PendingApproval);
        profile.CompanyName.Should().Be("Updated Company");
        profile.ContactAddress.Should().Be("Updated Address");
        profile.ContactPhone.Should().Be("0987654321");
        profile.RejectionReason.Should().BeNull();
        profile.ReviewedBy.Should().BeNull();
        profile.ReviewedAtUtc.Should().BeNull();

        // Old rejected document is retained
        profile.Documents.Should().Contain(d => d.Id == 101 && d.Status == DocumentStatus.Rejected);
        // New business license is added as Submitted
        profile.Documents.Should().Contain(d =>
            d.DocumentType == OperatorDocumentType.BusinessLicense &&
            d.Status == DocumentStatus.Submitted &&
            d.Id != 101);
        // New supporting document is added as Submitted
        profile.Documents.Should().Contain(d =>
            d.DocumentType == OperatorDocumentType.Other &&
            d.Status == DocumentStatus.Submitted);

        // Audit log recorded
        var audit = await db.AuditLogs.SingleAsync();
        audit.ActionType.Should().Be(AuditActionTypes.OperatorApplicationResubmit);
        audit.AffectedEntity.Should().Be("OperatorProfile");
        audit.AffectedEntityId.Should().Be(CurrentUserId);
        audit.BeforeData.Should().Contain("Unreadable license scan");
        audit.AfterData.Should().Contain(nameof(OperatorApprovalStatus.PendingApproval));

        deleted.Should().BeEmpty();
        db.FinalizedOperatorDocumentCleanupPublicIds.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_HappyPath_RetainingExistingRejectedLicense_ResetsLicenseToSubmitted()
    {
        SeedRejectedApplication();

        var result = await Handler().Handle(Command(license: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ApprovalStatus.Should().Be(nameof(OperatorApprovalStatus.PendingApproval));

        var profile = await db.OperatorProfiles.Include(p => p.Documents).SingleAsync(p => p.UserId == CurrentUserId);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.PendingApproval);
        profile.RejectionReason.Should().BeNull();

        // Retained license status reset to Submitted
        var licenseDoc = profile.Documents.Single(d => d.Id == 101);
        licenseDoc.Status.Should().Be(DocumentStatus.Submitted);

        // Audit log created
        db.AuditLogs.Should().ContainSingle(a => a.ActionType == AuditActionTypes.OperatorApplicationResubmit);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DuplicateTaxCodeOrLicenseFromOtherUser_ReturnsDuplicateIdentifierMsg159()
    {
        SeedRejectedApplication(taxCode: "0101234567", licenseNo: "79-0123/2026/TCDL-GPLHQT");

        // Another operator already has this tax code
        var otherUser = new User
        {
            Id = 99,
            Email = "other@example.com",
            FullName = "Other Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        db.Users.Add(otherUser);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = otherUser.Id,
            User = otherUser,
            CompanyName = "Other Co",
            TaxCode = "CONFLICT-TAX",
            BusinessLicenseNo = "79-9999/2026/TCDL-GPLHQT",
        });
        await db.SaveChangesAsync();

        var newLicenseDoc = new OperatorRegistrationDocument("new-license.pdf", "application/pdf", "%PDF-new"u8.ToArray());
        var result = await Handler().Handle(
            Command(license: newLicenseDoc, taxCode: "CONFLICT-TAX"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorApplicationErrorCodes.DuplicateIdentifier);

        // Uploaded file compensated
        deleted.Should().ContainSingle();
        var profile = await db.OperatorProfiles.SingleAsync(p => p.UserId == CurrentUserId);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Rejected);
    }

    [Fact]
    public async Task Handle_UploadFailure_CompensatesUploadedAndReturnsMsg127()
    {
        SeedRejectedApplication();

        var calls = 0;
        storage.Setup(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperatorDocumentStorageUpload upload, CancellationToken _) =>
                ++calls == 1
                    ? OperatorDocumentStorageUploadResult.Succeeded(new Uri($"https://cdn.example.com/{upload.PublicId}"))
                    : OperatorDocumentStorageUploadResult.Failed(TourMediaStorageFailureKind.Transient, "PROVIDER_DOWN"));

        var licenseDoc = new OperatorRegistrationDocument("license.pdf", "application/pdf", "%PDF-test"u8.ToArray());
        var supportingDoc = new OperatorRegistrationDocument("extra.png", "image/png", [137, 80, 78, 71, 13, 10, 26, 10]);

        var result = await Handler().Handle(
            Command(license: licenseDoc, supporting: [supportingDoc]),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorApplicationErrorCodes.Unavailable);
        deleted.Should().Contain("operators/resubmit-1.pdf");
        cleanup.Verify(x => x.RetryNowAsync("operators/resubmit-2", It.IsAny<CancellationToken>()), Times.Once);

        var profile = await db.OperatorProfiles.SingleAsync(p => p.UserId == CurrentUserId);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Rejected);
    }

    [Fact]
    public async Task Handle_SaveFailure_RollsBackAndCompensatesUploadedFiles()
    {
        SeedRejectedApplication();

        db.ThrowOnTransaction = new InvalidOperationException("DB write failure");
        var licenseDoc = new OperatorRegistrationDocument("license.pdf", "application/pdf", "%PDF-test"u8.ToArray());

        var result = await Handler().Handle(Command(license: licenseDoc), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorApplicationErrorCodes.Unavailable);
        deleted.Should().ContainSingle(id => id.EndsWith(".pdf"));

        db.ClearTrackedEntities();
        var profile = await db.OperatorProfiles.SingleAsync(p => p.UserId == CurrentUserId);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.Rejected);
    }

    [Fact]
    public async Task Handle_ConcurrencyConflictDuringSave_ReturnsMsg161AndCompensates()
    {
        SeedRejectedApplication();

        db.ThrowOnSaveConcurrency = true;
        var licenseDoc = new OperatorRegistrationDocument("license.pdf", "application/pdf", "%PDF-test"u8.ToArray());

        var result = await Handler().Handle(Command(license: licenseDoc), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorApplicationErrorCodes.NotRejected);
        deleted.Should().ContainSingle(id => id.EndsWith(".pdf"));
    }

    [Fact]
    public async Task Handle_UniqueConstraintViolationDuringSave_ReturnsMsg159AndCompensates()
    {
        SeedRejectedApplication();

        db.ThrowOnTransaction = new DbUpdateException("Duplicate key violation", (Exception?)null);
        constraints.Setup(x => x.Classify(It.IsAny<DbUpdateException>()))
            .Returns(OperatorRegistrationConstraint.TaxCodeOrBusinessLicense);

        var licenseDoc = new OperatorRegistrationDocument("license.pdf", "application/pdf", "%PDF-test"u8.ToArray());
        var result = await Handler().Handle(Command(license: licenseDoc), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorApplicationErrorCodes.DuplicateIdentifier);
        deleted.Should().ContainSingle(id => id.EndsWith(".pdf"));
    }
}
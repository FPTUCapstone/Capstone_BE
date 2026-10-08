using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.RegisterOperator;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.RegisterOperator;

public class RegisterOperatorCommandHandlerTests
{
    private readonly TestDbContext db = TestDbContext.Create();
    private readonly Mock<IFirebaseAuthService> firebase = new();
    private readonly Mock<IOperatorDocumentStorage> storage = new();
    private readonly Mock<IDateTimeProvider> clock = new();
    private readonly Mock<IOperatorRegistrationConstraintClassifier> constraints = new();
    private readonly FakePasswordHasher hasher = new();
    private readonly DateTimeOffset now = new(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);
    private readonly List<string> deleted = [];
    private int uploadCount;

    public RegisterOperatorCommandHandlerTests()
    {
        clock.Setup(x => x.UtcNow).Returns(now);
        firebase.Setup(x => x.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FirebaseTokenValidationResult("uid", "operator@example.com", false));
        storage.Setup(x => x.AllocatePublicId()).Returns(() => $"operators/{++uploadCount}");
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

    private RegisterOperatorCommandHandler Handler() => new(
        db, firebase.Object, hasher, clock.Object, storage.Object, constraints.Object,
        NullLogger<RegisterOperatorCommandHandler>.Instance);

    private static RegisterOperatorCommand Command(IReadOnlyList<OperatorRegistrationDocument>? extras = null) => new(
        "token", " Operator@Example.com ", "Password123!", "Password123!",
        " Company ", " LIC-1 ", " TAX-1 ", " Contact ", " Address ", "0912345678",
        new OperatorRegistrationDocument("lic.pdf", "application/pdf", "%PDF-test"u8.ToArray()),
        extras, true);

    [Fact]
    public async Task ValidRegistration_PersistsGraphInOneTransaction()
    {
        var result = await Handler().Handle(Command([
            new OperatorRegistrationDocument("extra.png", "image/png", [137, 80, 78, 71, 13, 10, 26, 10])]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        storage.Verify(x => x.UploadAsync(
            It.Is<OperatorDocumentStorageUpload>(upload =>
                upload.ContentType == "application/pdf" && upload.PublicId.EndsWith(".pdf")),
            It.IsAny<CancellationToken>()), Times.Once);
        storage.Verify(x => x.UploadAsync(
            It.Is<OperatorDocumentStorageUpload>(upload =>
                upload.ContentType == "image/png" && !upload.PublicId.EndsWith(".pdf")),
            It.IsAny<CancellationToken>()), Times.Once);
        result.Value.ApplicationStatus.Should().Be(nameof(OperatorApprovalStatus.PendingApproval));
        result.Value.MessageCode.Should().Be(AuthErrorCodes.Msg08);
        db.TransactionExecutionCount.Should().Be(1);
        db.SaveChangesAsyncCallCount.Should().Be(1);
        var user = db.Users.Single();
        user.Id.Should().Be(result.Value.UserId);
        user.Email.Should().Be("operator@example.com");
        user.FullName.Should().Be("Contact");
        user.PasswordHash.Should().Be("hashed:Password123!");
        user.Role.Should().Be(UserRole.TourOperator);
        user.Status.Should().Be(AccountStatus.PendingApproval);
        user.EmailVerifiedAtUtc.Should().BeNull();
        var profile = db.OperatorProfiles.Single();
        profile.UserId.Should().Be(user.Id);
        profile.ApprovalStatus.Should().Be(OperatorApprovalStatus.PendingApproval);
        profile.CompanyName.Should().Be("Company");
        profile.TaxCode.Should().Be("TAX-1");
        profile.BusinessLicenseNo.Should().Be("LIC-1");
        profile.ContactAddress.Should().Be("Address");
        profile.ContactPhone.Should().Be("0912345678");
        db.OperatorDocuments.Select(x => x.DocumentType).Should()
            .BeEquivalentTo([OperatorDocumentType.BusinessLicense, OperatorDocumentType.Other]);
        db.OperatorDocuments.Should().OnlyContain(x =>
            x.Status == DocumentStatus.Submitted &&
            x.OperatorUserId == user.Id &&
            x.FileUrl.StartsWith("https://cdn.example.com/operators/", StringComparison.Ordinal));
        deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task TokenEmailMismatch_DoesNotUploadOrPersist()
    {
        firebase.Setup(x => x.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FirebaseTokenValidationResult("uid", "other@example.com", false));
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthEmailMismatch);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
        db.Users.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidToken_DoesNotUploadOrPersist()
    {
        firebase.Setup(x => x.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider secret"));
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.AuthTokenInvalid);
        result.ErrorMessage.Should().NotContain("secret");
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, AuthErrorCodes.Msg160)]
    [InlineData(false, AuthErrorCodes.Msg03)]
    public async Task ExistingEmail_UsesPendingPrecedence(bool pending, string expectedCode)
    {
        var user = new User
        {
            Email = "operator@example.com",
            FullName = "Old",
            Role = UserRole.TourOperator,
            Status = pending ? AccountStatus.PendingApproval : AccountStatus.Active,
        };
        db.Users.Add(user);
        if (pending)
        {
            db.OperatorProfiles.Add(new OperatorProfile
            {
                User = user,
                CompanyName = "Old",
                TaxCode = "OLD-TAX",
                BusinessLicenseNo = "OLD-LIC",
            });
        }
        await db.SaveChangesAsync();
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(expectedCode);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PendingUserWithoutApplication_IsDuplicateEmailNotPendingApplication()
    {
        db.Users.Add(new User
        {
            Email = "operator@example.com",
            FullName = "Old",
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingApproval,
        });
        await db.SaveChangesAsync();
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg03);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingTaxOrLicence_ReturnsMsg159(bool tax)
    {
        db.Users.Add(new User { Email = "old@example.com", FullName = "Old" });
        db.OperatorProfiles.Add(new OperatorProfile
        {
            User = db.Users.Local.Single(),
            CompanyName = "Old",
            TaxCode = tax ? "TAX-1" : "different",
            BusinessLicenseNo = tax ? "different" : "LIC-1",
        });
        await db.SaveChangesAsync();
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg159);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SecondUploadFailure_CompensatesUploadedFiles()
    {
        var calls = 0;
        storage.Setup(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperatorDocumentStorageUpload upload, CancellationToken _) =>
                ++calls == 1
                    ? OperatorDocumentStorageUploadResult.Succeeded(new Uri($"https://cdn.example.com/{upload.PublicId}"))
                    : OperatorDocumentStorageUploadResult.Failed(TourMediaStorageFailureKind.Transient, "SAFE"));
        var result = await Handler().Handle(Command([
            new OperatorRegistrationDocument("extra.pdf", "application/pdf", "%PDF-test"u8.ToArray())]), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        deleted.Should().BeEquivalentTo(["operators/1.pdf", "operators/2.pdf"]);
        db.Users.Should().BeEmpty();
    }

    [Fact]
    public async Task UploadException_CompensatesAllocatedDocument()
    {
        storage.Setup(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider secret"));
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        result.ErrorMessage.Should().NotContain("secret");
        deleted.Should().BeEquivalentTo(["operators/1.pdf"]);
        db.Users.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveFailure_CompensatesAndReturnsGenericError()
    {
        db.ThrowOnSaveConcurrency = true;
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        deleted.Should().BeEquivalentTo(["operators/1.pdf"]);
        db.ClearTrackedEntities();
        db.Users.Should().BeEmpty();
    }

    [Theory]
    [InlineData(OperatorRegistrationConstraint.Email, AuthErrorCodes.Msg03)]
    [InlineData(OperatorRegistrationConstraint.TaxCodeOrBusinessLicense, AuthErrorCodes.Msg159)]
    public async Task UniqueIndexRace_CompensatesAndReturnsSpecificConflict(
        OperatorRegistrationConstraint violation, string expectedCode)
    {
        db.ThrowOnSaveConcurrency = true;
        constraints.Setup(x => x.Classify(It.IsAny<DbUpdateException>())).Returns(violation);
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(expectedCode);
        deleted.Should().BeEquivalentTo(["operators/1.pdf"]);
    }

    [Fact]
    public async Task ConcurrentPendingApplication_EmailIndexRaceReturnsMsg160()
    {
        db.ThrowOnSaveConcurrency = true;
        constraints.Setup(x => x.Classify(It.IsAny<DbUpdateException>()))
            .Returns(() =>
            {
                var winner = new User
                {
                    Email = "operator@example.com",
                    FullName = "Winner",
                    Role = UserRole.TourOperator,
                    Status = AccountStatus.PendingApproval,
                };
                db.OperatorProfiles.Add(new OperatorProfile
                {
                    User = winner,
                    CompanyName = "Winner",
                    TaxCode = "WINNER-TAX",
                    BusinessLicenseNo = "WINNER-LIC",
                });
                db.SaveChanges();
                return OperatorRegistrationConstraint.Email;
            });

        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg160);
        deleted.Should().BeEquivalentTo(["operators/1.pdf"]);
    }

    [Fact]
    public async Task CompensationFailure_DoesNotMaskOriginalStorageFailure()
    {
        storage.Setup(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorDocumentStorageUploadResult.Failed(
                TourMediaStorageFailureKind.Transient, "SAFE"));
        storage.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("delete unavailable"));
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        db.Users.Should().BeEmpty();
    }

    [Fact]
    public async Task FirebaseUnavailable_Returns503CodeWithoutUploading()
    {
        firebase.Setup(x => x.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FirebaseUnavailableException("provider unavailable"));
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DatabasePrecheckFailure_ReturnsGenericFailureBeforeUpload()
    {
        db.Dispose();
        var result = await Handler().Handle(Command(), CancellationToken.None);
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        storage.Verify(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancellationAfterUploadStarted_CompensatesBeforePropagating()
    {
        storage.Setup(x => x.UploadAsync(It.IsAny<OperatorDocumentStorageUpload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        Func<Task> act = () => Handler().Handle(Command(), CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
        deleted.Should().BeEquivalentTo(["operators/1.pdf"]);
    }
}
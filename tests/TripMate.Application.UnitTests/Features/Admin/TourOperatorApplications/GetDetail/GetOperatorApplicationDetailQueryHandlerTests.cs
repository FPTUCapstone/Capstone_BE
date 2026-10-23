using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.Admin.TourOperatorApplications.Common;
using TripMate.Application.Features.Admin.TourOperatorApplications.GetDetail;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Admin.TourOperatorApplications.GetDetail;

public class GetOperatorApplicationDetailQueryHandlerTests
{
    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly Mock<IOperatorDocumentStorage> _storage = new();
    private readonly Mock<IDateTimeProvider> _clock = new();
    private readonly GetOperatorApplicationDetailQueryHandler _handler;

    public GetOperatorApplicationDetailQueryHandlerTests()
    {
        _clock.SetupGet(clock => clock.UtcNow)
            .Returns(DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
        _handler = new GetOperatorApplicationDetailQueryHandler(
            _dbContext, _storage.Object, _clock.Object);
    }

    [Fact]
    public async Task Handle_WhenUserDoesNotExist_ShouldReturnNotFoundFailure()
    {
        // Arrange
        var query = new GetOperatorApplicationDetailQuery(999);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_WhenUserRoleIsNotTourOperator_ShouldReturnWrongRoleFailure()
    {
        // Arrange
        var user = new User
        {
            FullName = "Traveler John",
            Email = "john@example.com",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var query = new GetOperatorApplicationDetailQuery(user.Id);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.WrongRole);
    }

    [Fact]
    public async Task Handle_WhenProfileDoesNotExist_ShouldReturnNotFoundFailure()
    {
        // Arrange
        var user = new User
        {
            FullName = "Operator Without Profile",
            Email = "noprofile@example.com",
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingApproval,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var query = new GetOperatorApplicationDetailQuery(user.Id);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(TourOperatorApplicationErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_WhenValidPendingApplication_ShouldReturnDetailWithoutSensitiveData()
    {
        // Arrange
        var user = new User
        {
            FullName = "Da Nang Tours Co",
            Email = "info@danangtours.com",
            PasswordHash = "SECRET_HASH_DO_NOT_EXPOSE",
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingApproval,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var profile = new OperatorProfile
        {
            User = user,
            CompanyName = "Da Nang Tours Co Ltd",
            TaxCode = "TAX-12345",
            BusinessLicenseNo = "LIC-998877",
            ContactPhone = "0901234567",
            ContactAddress = "Da Nang, Vietnam",
            ApprovalStatus = OperatorApprovalStatus.PendingApproval,
        };
        _dbContext.OperatorProfiles.Add(profile);

        _dbContext.OperatorDocuments.Add(new OperatorDocument
        {
            OperatorProfile = profile,
            DocumentType = OperatorDocumentType.BusinessLicense,
            FileUrl = "https://storage.tripmate.vn/docs/license.pdf",
            Status = DocumentStatus.Submitted,
        });

        _dbContext.OperatorDocuments.Add(new OperatorDocument
        {
            OperatorProfile = profile,
            DocumentType = OperatorDocumentType.TaxCode,
            FileUrl = "https://storage.tripmate.vn/docs/tax.pdf",
            Status = DocumentStatus.Submitted,
        });

        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var query = new GetOperatorApplicationDetailQuery(user.Id);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;
        dto.UserId.Should().Be(user.Id);
        dto.Role.Should().Be(UserRole.TourOperator);
        dto.AccountStatus.Should().Be(AccountStatus.PendingApproval);
        dto.ApplicationStatus.Should().Be(OperatorApprovalStatus.PendingApproval);
        dto.CompanyName.Should().Be("Da Nang Tours Co Ltd");
        dto.TaxCode.Should().Be("TAX-12345");
        dto.BusinessLicenseNumber.Should().Be("LIC-998877");
        dto.ContactPhone.Should().Be("0901234567");
        dto.ContactAddress.Should().Be("Da Nang, Vietnam");
        dto.Documents.Count.Should().Be(2);
        dto.Documents.Should().OnlyContain(document => document.FileUrl == string.Empty,
            "a legacy public URL cannot be safely returned as a document download link");
        dto.ReviewedBy.Should().BeNull();
        dto.ReviewedAt.Should().BeNull();
    }

    [Fact]
    public async Task Handle_PrivateDocument_ReturnsOnlyShortLivedSignedLink()
    {
        const string storedReference = "cloudinary-operator://asset/raw/pdf/tripmate%2Foperator-documents%2Fopaque";
        const string signedUrl = "https://api.cloudinary.com/download?expires_at=1791461100&signature=test";
        var user = new User
        {
            FullName = "Operator",
            Email = "operator@example.com",
            Role = UserRole.TourOperator,
            Status = AccountStatus.PendingApproval,
        };
        var profile = new OperatorProfile
        {
            User = user,
            CompanyName = "Operator Co",
            TaxCode = "0101234567",
            BusinessLicenseNo = "79-0123/2026/TCDL-GPLHQT",
            ApprovalStatus = OperatorApprovalStatus.PendingApproval,
        };
        _dbContext.Users.Add(user);
        _dbContext.OperatorProfiles.Add(profile);
        _dbContext.OperatorDocuments.Add(new OperatorDocument
        {
            OperatorProfile = profile,
            DocumentType = OperatorDocumentType.BusinessLicense,
            FileUrl = storedReference,
            Status = DocumentStatus.Submitted,
        });
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        var expiresAt = _clock.Object.UtcNow.AddMinutes(5);
        _storage.Setup(storage => storage.CreateTemporaryDownloadUrl(storedReference, expiresAt))
            .Returns(new Uri(signedUrl));

        var result = await _handler.Handle(
            new GetOperatorApplicationDetailQuery(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Documents.Should().ContainSingle()
            .Which.FileUrl.Should().Be(signedUrl);
        _storage.Verify(storage => storage.CreateTemporaryDownloadUrl(storedReference, expiresAt),
            Times.Once);
    }
}
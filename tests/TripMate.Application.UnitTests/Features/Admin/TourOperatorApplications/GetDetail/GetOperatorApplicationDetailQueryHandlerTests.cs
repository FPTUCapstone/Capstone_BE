using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
    private readonly GetOperatorApplicationDetailQueryHandler _handler;

    public GetOperatorApplicationDetailQueryHandlerTests()
    {
        _handler = new GetOperatorApplicationDetailQueryHandler(_dbContext);
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
        dto.ReviewedBy.Should().BeNull();
        dto.ReviewedAt.Should().BeNull();
    }
}

using FluentAssertions;

using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.PasswordReset;

using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.PasswordReset;

public class PasswordResetEligibilityResolverTests
{
    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly PasswordResetEligibilityResolver _resolver;

    public PasswordResetEligibilityResolverTests()
    {
        _resolver = new PasswordResetEligibilityResolver(_dbContext);
    }

    private User CreateUser(
        string email,
        AccountStatus status,
        UserRole role = UserRole.Traveler,
        string? passwordHash = "hashed-password")
    {
        var user = new User
        {
            Email = email,
            FullName = "Test User",
            Role = role,
            Status = status,
            PasswordHash = passwordHash,
        };
        _dbContext.Users.Add(user);
        _dbContext.SaveChanges();
        return user;
    }

    [Fact(DisplayName = "PLAN-ELIG-01: Active local-password account is eligible")]
    public async Task ActiveLocalPasswordAccount_IsEligible()
    {
        var user = CreateUser("active@example.com", AccountStatus.Active);

        var result = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "active@example.com", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(user.Id);
    }

    [Fact(DisplayName = "PLAN-ELIG-02: PendingEmailVerification local-password account is eligible")]
    public async Task PendingEmailVerificationLocalPasswordAccount_IsEligible()
    {
        var user = CreateUser("pending@example.com", AccountStatus.PendingEmailVerification);

        var result = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "pending@example.com", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(user.Id);
    }

    [Fact(DisplayName = "PLAN-ELIG-03: Administrator local-password account is eligible through the shared flow")]
    public async Task AdministratorLocalPasswordAccount_IsEligible()
    {
        var user = CreateUser("admin@example.com", AccountStatus.Active, UserRole.Administrator);

        var result = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "admin@example.com", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(user.Id);
    }

    [Fact(DisplayName = "PLAN-ELIG-04: Locked account is ineligible")]
    public async Task LockedAccount_IsIneligible()
    {
        CreateUser("locked@example.com", AccountStatus.Locked);

        var result = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "locked@example.com", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact(DisplayName = "PLAN-ELIG-05: Inactive account is ineligible")]
    public async Task InactiveAccount_IsIneligible()
    {
        CreateUser("inactive@example.com", AccountStatus.Inactive);

        var result = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "inactive@example.com", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact(DisplayName = "PLAN-ELIG-06: Google-only account (password_hash null) is ineligible")]
    public async Task GoogleOnlyAccount_IsIneligible()
    {
        CreateUser("google@example.com", AccountStatus.Active, passwordHash: null);

        var result = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "google@example.com", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact(DisplayName = "PLAN-ELIG-07: TourOperator approval status does not change eligibility")]
    public async Task TourOperatorApprovalStatus_DoesNotChangeEligibility()
    {
        var approvedOperator = CreateUser("approved-op@example.com", AccountStatus.Active, UserRole.TourOperator);
        var rejectedOperator = CreateUser("rejected-op@example.com", AccountStatus.Active, UserRole.TourOperator);
        _dbContext.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = approvedOperator.Id,
            User = approvedOperator,
            CompanyName = "Approved Co",
            TaxCode = "T1",
            BusinessLicenseNo = "B1",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        _dbContext.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = rejectedOperator.Id,
            User = rejectedOperator,
            CompanyName = "Rejected Co",
            TaxCode = "T2",
            BusinessLicenseNo = "B2",
            ApprovalStatus = OperatorApprovalStatus.Rejected,
        });
        _dbContext.SaveChanges();

        var approvedResult = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "approved-op@example.com", CancellationToken.None);
        var rejectedResult = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "rejected-op@example.com", CancellationToken.None);

        approvedResult.IsSuccess.Should().BeTrue();
        rejectedResult.IsSuccess.Should().BeTrue();
    }

    [Fact(DisplayName = "PLAN-ELIG-08: email matching is case-insensitive and trims input")]
    public async Task EmailNormalization_IsApplied()
    {
        var user = CreateUser("normalized@example.com", AccountStatus.Active);

        var result = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "  Normalized@Example.COM ", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(user.Id);
    }

    [Fact(DisplayName = "PLAN-ELIG-09: unknown email fails generically")]
    public async Task UnknownEmail_FailsGenerically()
    {
        var result = await _resolver.ResolveEligibleLocalPasswordAccountAsync(
            "unknown@example.com", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg14);
    }
}
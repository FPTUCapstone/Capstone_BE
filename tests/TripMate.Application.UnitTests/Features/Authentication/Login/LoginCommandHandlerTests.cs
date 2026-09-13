using FluentAssertions;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.Login;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.Login;

public class LoginCommandHandlerTests
{
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeJwtTokenService _jwtTokenService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();

    [Fact]
    public async Task Handle_WithUnknownEmail_ReturnsGenericInvalidCredentials()
    {
        await using var dbContext = TestDbContext.Create();
        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand("nobody@example.com", "Passw0rd123"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task Handle_WithWrongPassword_ReturnsGenericInvalidCredentials()
    {
        await using var dbContext = TestDbContext.Create();
        await SeedUser(dbContext, "user@example.com", "CorrectPass1", AccountStatus.Active);
        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand("user@example.com", "WrongPass1"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.InvalidCredentials);
    }

    [Theory]
    [InlineData(AccountStatus.PendingEmailVerification, AuthErrorCodes.AccountPendingVerification)]
    [InlineData(AccountStatus.Locked, AuthErrorCodes.AccountLocked)]
    [InlineData(AccountStatus.Inactive, AuthErrorCodes.AccountInactive)]
    public async Task Handle_WithBlockingAccountStatus_BlocksSignInIndependentlyOfCredentials(
        AccountStatus status,
        string expectedErrorCode)
    {
        await using var dbContext = TestDbContext.Create();
        await SeedUser(dbContext, "user@example.com", "CorrectPass1", status);
        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand("user@example.com", "CorrectPass1"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedErrorCode);
    }

    [Theory]
    [InlineData(AccountStatus.PendingApproval)]
    [InlineData(AccountStatus.Rejected)]
    public async Task Handle_WithTourOperatorApplicationStatus_StillSignsIn(AccountStatus status)
    {
        // BR-07/BR-09: a Tour Operator account in PendingApproval or Rejected status must still
        // be able to sign in — Rejected in particular is a hard dependency for UC-03
        // (resubmission requires signing in first). That status only gates the Tour Operator
        // workspace downstream, never authentication itself.
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "operator@example.com", "CorrectPass1", status, UserRole.TourOperator);

        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand("operator@example.com", "CorrectPass1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(UserRole.TourOperator);
        result.Value.Status.Should().Be(status);
        result.Value.UserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task Handle_WithValidActiveAccount_IssuesSessionAndPersistsHashedRefreshToken()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "user@example.com", "CorrectPass1", AccountStatus.Active);
        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand("user@example.com", "CorrectPass1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().NotBeNullOrWhiteSpace();
        result.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();

        var persistedUser = await dbContext.Users.FindAsync(user.Id);
        var expectedHash = _jwtTokenService.HashRefreshToken(result.Value.RefreshToken);
        persistedUser!.RefreshTokens.Should().ContainSingle(t => t.TokenHash == expectedHash);
    }

    [Fact]
    public async Task Handle_WithValidActiveAccount_RecordsLastLoginTimestamp()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "user@example.com", "CorrectPass1", AccountStatus.Active);
        var handler = CreateHandler(dbContext);

        await handler.Handle(new LoginCommand("user@example.com", "CorrectPass1"), CancellationToken.None);

        var persistedUser = await dbContext.Users.FindAsync(user.Id);
        persistedUser!.LastLoginAtUtc.Should().Be(_dateTimeProvider.UtcNow);
    }

    private LoginCommandHandler CreateHandler(TestDbContext dbContext) =>
        new(dbContext, _passwordHasher, _jwtTokenService, _dateTimeProvider);

    private async Task<User> SeedUser(
        TestDbContext dbContext,
        string email,
        string password,
        AccountStatus status,
        UserRole role = UserRole.Traveler)
    {
        var user = new User
        {
            Email = email,
            FullName = "Test User",
            PasswordHash = _passwordHasher.Hash(password),
            Role = role,
            Status = status,
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            UpdatedAtUtc = _dateTimeProvider.UtcNow,
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return user;
    }
}

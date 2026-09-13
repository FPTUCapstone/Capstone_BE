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
        result.ErrorMessage.Should().Be("Invalid email or password.");
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
        result.ErrorMessage.Should().Be("Invalid email or password.");
    }

    [Fact]
    public async Task Handle_WithoutPasswordHash_ReturnsSameGenericInvalidCredentials()
    {
        // UC-04 §4.1: the no-password case must be indistinguishable from a wrong password —
        // same code, same message, no hint that the account exists.
        await using var dbContext = TestDbContext.Create();
        var user = new User
        {
            Email = "nopassword@example.com",
            FullName = "No Password User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            PasswordHash = null,
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            UpdatedAtUtc = _dateTimeProvider.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(CancellationToken.None);
        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand("nopassword@example.com", "WhateverPass1"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.InvalidCredentials);
        result.ErrorMessage.Should().Be("Invalid email or password.");
    }

    [Theory]
    [InlineData(AccountStatus.PendingEmailVerification, AuthErrorCodes.AccountPendingVerification, "Email has not been verified.")]
    [InlineData(AccountStatus.PendingApproval, AuthErrorCodes.AccountPendingApproval, "Account is pending approval.")]
    [InlineData(AccountStatus.Locked, AuthErrorCodes.AccountLocked, "Account is locked.")]
    [InlineData(AccountStatus.Inactive, AuthErrorCodes.AccountInactive, "Account is inactive.")]
    public async Task Handle_WithBlockingAccountStatus_BlocksSignInIndependentlyOfCredentials(
        AccountStatus status,
        string expectedErrorCode,
        string expectedMessage)
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "user@example.com", "CorrectPass1", status);
        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand("user@example.com", "CorrectPass1"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedErrorCode);
        result.ErrorMessage.Should().Be(expectedMessage);

        // BR-10: every failure path mutates nothing — no session record, no last-login stamp.
        var persistedUser = await dbContext.Users.FindAsync(user.Id);
        persistedUser!.LastLoginAtUtc.Should().BeNull();
        persistedUser.RefreshTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WithRejectedTourOperator_StillSignsInWithoutStatusChange()
    {
        // BR-06: a Rejected Tour Operator may still sign in (UC-03 resubmission requires a
        // session). The status stays Rejected — Sign In never flips it — and the role comes
        // from the database. PendingApproval, by contrast, is blocked per BR-05.
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "operator@example.com", "CorrectPass1", AccountStatus.Rejected, UserRole.TourOperator);

        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand("operator@example.com", "CorrectPass1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(UserRole.TourOperator);
        result.Value.Status.Should().Be(AccountStatus.Rejected);
        result.Value.UserId.Should().Be(user.Id);

        var persistedUser = await dbContext.Users.FindAsync(user.Id);
        persistedUser!.Status.Should().Be(AccountStatus.Rejected);
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

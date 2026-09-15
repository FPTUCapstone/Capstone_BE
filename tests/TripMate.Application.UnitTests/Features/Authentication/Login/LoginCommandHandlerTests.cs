using System.Text.Json;
using System.Text.Json.Serialization;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task Handle_WithActiveAdministratorPassword_StillIssuesSession()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "admin@example.com", "CorrectPass1", AccountStatus.Active, UserRole.Administrator);
        var result = await CreateHandler(dbContext).Handle(
            new LoginCommand("admin@example.com", "CorrectPass1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(UserRole.Administrator);
        result.Value.AccessToken.Should().NotBeNullOrWhiteSpace();
        dbContext.RefreshTokens.Should().ContainSingle(t => t.UserId == user.Id);
        user.LastLoginAtUtc.Should().NotBeNull();
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
    [InlineData(AccountStatus.PendingApproval, AuthErrorCodes.AccountStateUnresolved, "Account state could not be resolved.")]
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
        // Approved legacy compatibility requires persisted evidence and a matching profile.
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "operator@example.com", "CorrectPass1", AccountStatus.Rejected, UserRole.TourOperator);

        user.EmailVerifiedAtUtc = user.CreatedAtUtc;
        dbContext.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = OperatorApprovalStatus.Rejected });
        await dbContext.SaveChangesAsync(CancellationToken.None);
        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(
            new LoginCommand("operator@example.com", "CorrectPass1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(UserRole.TourOperator);
        result.Value.Status.Should().Be(AccountStatus.Active);
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

    [Theory]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.PendingApproval)]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.Rejected)]
    public async Task Handle_WithVerifiedMatchingLegacyOperator_ReturnsEffectiveActiveWithoutNormalizingDatabase(
        AccountStatus status, OperatorApprovalStatus approval)
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "legacy@example.com", "CorrectPass1", status, UserRole.TourOperator);
        user.EmailVerifiedAtUtc = user.CreatedAtUtc;
        dbContext.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = approval });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler(dbContext).Handle(
            new LoginCommand(user.Email!, "CorrectPass1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(AccountStatus.Active);
        user.Status.Should().Be(status);
        dbContext.OperatorProfiles.Single().ApprovalStatus.Should().Be(approval);
        dbContext.RefreshTokens.Should().ContainSingle(t => t.UserId == user.Id);
    }

    [Theory]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.Approved, UserRole.TourOperator, 0, true)]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.PendingApproval, UserRole.TourOperator, 0, true)]
    [InlineData(AccountStatus.PendingApproval, (OperatorApprovalStatus)99, UserRole.TourOperator, 0, true)]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.Rejected, UserRole.Traveler, 0, true)]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.PendingApproval, UserRole.Administrator, 0, true)]
    [InlineData(AccountStatus.PendingApproval, OperatorApprovalStatus.PendingApproval, UserRole.TourOperator, 0, false)]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.Rejected, UserRole.TourOperator, 2, true)]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.Rejected, UserRole.TourOperator, -1, true)]
    [InlineData(AccountStatus.Rejected, OperatorApprovalStatus.Rejected, UserRole.TourOperator, 1, true)]
    [InlineData((AccountStatus)99, OperatorApprovalStatus.Approved, UserRole.TourOperator, 0, true)]
    public async Task Handle_WithUnresolvableLegacyState_DeniesWithoutMutation(
        AccountStatus status, OperatorApprovalStatus approval, UserRole role, int evidence, bool hasProfile)
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "unresolved@example.com", "CorrectPass1", status, role);
        user.CreatedAtUtc = _dateTimeProvider.UtcNow.AddDays(-1);
        user.EmailVerifiedAtUtc = evidence == 2 ? null : evidence == -1
            ? user.CreatedAtUtc.AddTicks(-1) : evidence == 1
            ? _dateTimeProvider.UtcNow.AddTicks(1) : _dateTimeProvider.UtcNow;
        if (hasProfile)
            dbContext.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = approval });
        await dbContext.SaveChangesAsync(CancellationToken.None);
        var verifiedAt = user.EmailVerifiedAtUtc;
        var updatedAt = user.UpdatedAtUtc;

        var result = await CreateHandler(dbContext).Handle(
            new LoginCommand(user.Email!, "CorrectPass1"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("auth.account_state_unresolved");
        dbContext.RefreshTokens.Should().BeEmpty();
        user.LastLoginAtUtc.Should().BeNull();
        user.Status.Should().Be(status);
        user.EmailVerifiedAtUtc.Should().Be(verifiedAt);
        user.UpdatedAtUtc.Should().Be(updatedAt);
        if (hasProfile) dbContext.OperatorProfiles.Single().ApprovalStatus.Should().Be(approval);
    }

    [Theory]
    [InlineData(UserRole.Traveler, null, null)]
    [InlineData(UserRole.Administrator, null, null)]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.Approved, "Approved")]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.PendingApproval, "PendingApproval")]
    [InlineData(UserRole.TourOperator, OperatorApprovalStatus.Rejected, "Rejected")]
    [InlineData(UserRole.TourOperator, null, null)]
    [InlineData(UserRole.TourOperator, (OperatorApprovalStatus)99, null)]
    public async Task Handle_ReturnsCurrentApplicationContextWithoutInvalidatingActiveSession(
        UserRole role, OperatorApprovalStatus? approval, string? expected)
    {
        await using var db = TestDbContext.Create();
        var user = await SeedUser(db, "context@example.com", "CorrectPass1", AccountStatus.Active, role);
        if (approval.HasValue) db.OperatorProfiles.Add(new OperatorProfile { UserId = user.Id, ApprovalStatus = approval.Value });
        await db.SaveChangesAsync(CancellationToken.None);
        var result = await CreateHandler(db).Handle(new LoginCommand(user.Email!, "CorrectPass1"), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.ApplicationStatus.Should().Be(expected);
        db.RefreshTokens.Should().ContainSingle(t => t.UserId == user.Id);
    }

    [Theory]
    [InlineData(UserRole.Traveler, null)]
    [InlineData(UserRole.Administrator, null)]
    [InlineData(UserRole.TourOperator, null)]
    [InlineData(UserRole.TourOperator, "Approved")]
    [InlineData(UserRole.TourOperator, "PendingApproval")]
    [InlineData(UserRole.TourOperator, "Rejected")]
    public void WebContext_SerializesRequiredApplicationStatusWithoutRefreshCredential(UserRole role, string? application)
    {
        var session = new AuthResponseDto(123, "user@example.com", "", role, AccountStatus.Active,
            "access", "private-refresh", DateTimeOffset.Parse("2026-09-14T10:15:00Z"))
        { ApplicationStatus = application };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        options.Converters.Add(new JsonStringEnumConverter());
        using var web = JsonDocument.Parse(JsonSerializer.Serialize(WebAuthResponseDto.From(session), options));
        web.RootElement.GetProperty("applicationStatus").GetString().Should().Be(application);
        web.RootElement.TryGetProperty("refreshToken", out _).Should().BeFalse();
        web.RootElement.GetProperty("fullName").GetString().Should().Be("");
        web.RootElement.GetProperty("userId").GetInt64().Should().Be(123);
        web.RootElement.GetProperty("role").GetString().Should().Be(role.ToString());
        using var mobile = JsonDocument.Parse(JsonSerializer.Serialize(session, options));
        mobile.RootElement.GetProperty("refreshToken").GetString().Should().Be("private-refresh");
        mobile.RootElement.TryGetProperty("applicationStatus", out _).Should().BeFalse();
    }

    private LoginCommandHandler CreateHandler(TestDbContext dbContext) =>
        new(dbContext, _passwordHasher, _jwtTokenService, _dateTimeProvider,
            NullLogger<LoginCommandHandler>.Instance);

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
using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Authentication.ChangePassword;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.ChangePassword;

public class ChangePasswordCommandHandlerTests
{
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeJwtTokenService _jwtTokenService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();

    [Fact]
    public async Task Handle_WithCorrectCurrentAndValidDistinctNewPassword_ReplacesHashAndIssuesOneReplacementSession()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        var oldHash = user.PasswordHash;
        await SeedRefreshToken(dbContext, user.Id, revoked: false);
        await SeedRefreshToken(dbContext, user.Id, revoked: false);

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.PasswordHash.Should().NotBe(oldHash);
        user.PasswordHash.Should().Be(_passwordHasher.Hash("NewPass2@"));
        user.UpdatedAtUtc.Should().Be(_dateTimeProvider.UtcNow);

        var tokens = await dbContext.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync();
        tokens.Should().HaveCount(3);
        tokens.Where(t => t.RevokedAtUtc == null).Should().ContainSingle();
        tokens.Count(t => t.RevokedAtUtc is not null).Should().Be(2);

        result.Value.AccessToken.Should().Be($"access-token-for-{user.Id}");
        result.Value.RefreshToken.Should().NotBeNullOrEmpty();
        _jwtTokenService.AccessTokenGenerationCount.Should().Be(1);
        _jwtTokenService.RefreshTokenGenerationCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_OnSuccess_RecordsAuditEventWithNoSecretData()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var audit = await dbContext.AuditLogs.SingleAsync(a => a.ActorUserId == user.Id);
        audit.ActionType.Should().Be(TripMate.Domain.Common.AuditActionTypes.AuthChangePassword);
        audit.AffectedEntityId.Should().Be(user.Id);
        audit.BeforeData.Should().BeNull();
        (audit.AfterData ?? string.Empty).Should().NotContain("CurrentPass1!")
            .And.NotContain("NewPass2@")
            .And.NotContain(user.PasswordHash!)
            .And.NotContain(result.Value.AccessToken)
            .And.NotContain(result.Value.RefreshToken);
    }

    [Fact]
    public async Task Handle_WithWrongCurrentPassword_ReturnsMsg17AndMakesNoMutation()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        var oldHash = user.PasswordHash;
        await SeedRefreshToken(dbContext, user.Id, revoked: false);

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("WrongPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg17);
        user.PasswordHash.Should().Be(oldHash);
        (await dbContext.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync())
            .Should().ContainSingle(t => t.RevokedAtUtc == null);
        _jwtTokenService.AccessTokenGenerationCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithNewPasswordEqualToCurrent_ReturnsMsg05AndMakesNoMutation()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        var oldHash = user.PasswordHash;

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "CurrentPass1!", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg05);
        user.PasswordHash.Should().Be(oldHash);
        _jwtTokenService.AccessTokenGenerationCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithNoLocalPassword_ReturnsLocalPasswordUnavailableAndMakesNoMutation()
    {
        await using var dbContext = TestDbContext.Create();
        var user = new User
        {
            Email = "google-only@example.com",
            FullName = "Google Only",
            PasswordHash = null,
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            UpdatedAtUtc = _dateTimeProvider.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("Anything1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.LocalPasswordUnavailable);
        user.PasswordHash.Should().BeNull();
        _jwtTokenService.AccessTokenGenerationCount.Should().Be(0);
    }

    [Theory]
    [InlineData(AccountStatus.Locked, AuthErrorCodes.AccountLocked)]
    [InlineData(AccountStatus.Inactive, AuthErrorCodes.AccountInactive)]
    [InlineData(AccountStatus.PendingEmailVerification, AuthErrorCodes.AccountPendingVerification)]
    public async Task Handle_WithIneligibleAccountStatus_ReturnsEligibilityFailureAndMakesNoMutation(
        AccountStatus status, string expectedErrorCode)
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!", status: status);
        var oldHash = user.PasswordHash;
        await SeedRefreshToken(dbContext, user.Id, revoked: false);

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedErrorCode);
        user.PasswordHash.Should().Be(oldHash);
        (await dbContext.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync())
            .Should().ContainSingle(t => t.RevokedAtUtc == null);
        _jwtTokenService.AccessTokenGenerationCount.Should().Be(0);
        dbContext.TransactionExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenAccessTokenGenerationFails_RollsBackBeforeAnyMutationAndReturnsMsg127()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        var oldHash = user.PasswordHash;
        await SeedRefreshToken(dbContext, user.Id, revoked: false);
        _jwtTokenService.ThrowOnGenerateAccessToken =
            new InvalidOperationException("Simulated signing-key failure.");

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        user.PasswordHash.Should().Be(oldHash);
        (await dbContext.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync())
            .Should().ContainSingle(t => t.RevokedAtUtc == null);
        // The signing failure happens before the transaction is ever opened: no partial
        // commit of the new hash/revoked-token state with no session to show for it.
        dbContext.TransactionExecutionCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithUnknownSubjectUserId_ReturnsLocalPasswordUnavailable()
    {
        await using var dbContext = TestDbContext.Create();

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("Anything1!", "NewPass2@", 999),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.LocalPasswordUnavailable);
    }

    [Fact]
    public async Task Handle_WhenTransactionThrows_RollsBackAndReturnsMsg127WithNoMutation()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        var oldHash = user.PasswordHash;
        await SeedRefreshToken(dbContext, user.Id, revoked: false);
        dbContext.ThrowOnTransaction = new InvalidOperationException("Simulated revoke/insert failure.");

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        user.PasswordHash.Should().Be(oldHash);
        (await dbContext.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync())
            .Should().ContainSingle(t => t.RevokedAtUtc == null);
        // Signing is deliberately attempted before opening the transaction. A later
        // transaction failure must still roll back every persisted mutation and return
        // no credentials, even though a short-lived token value was generated in memory.
        _jwtTokenService.AccessTokenGenerationCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenCommitFails_ReturnsMsg127AndDoesNotReturnCredentials()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        dbContext.TransactionCompletionFailureFactory =
            _ => new InvalidOperationException("Simulated commit failure.");

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        // The access token is prepared before the transaction so signing cannot fail
        // after commit. Commit failure returns no credentials and persists no mutation.
        _jwtTokenService.AccessTokenGenerationCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_ForApprovedTourOperator_ReturnsActiveStatusAndApprovedApplicationStatus()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!", role: UserRole.TourOperator);
        dbContext.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            CompanyName = "Acme Tours",
            TaxCode = "TAX-001",
            BusinessLicenseNo = "LIC-001",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            UpdatedAtUtc = _dateTimeProvider.UtcNow,
        });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.ApplicationStatus.Should().Be("Approved");
    }

    [Fact]
    public async Task Handle_ForPendingApprovalTourOperator_ReturnsActiveStatusAndPendingApplicationStatus()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(
            dbContext, "CurrentPass1!", role: UserRole.TourOperator, status: AccountStatus.PendingApproval);
        user.EmailVerifiedAtUtc = _dateTimeProvider.UtcNow;
        dbContext.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            CompanyName = "Acme Tours",
            TaxCode = "TAX-002",
            BusinessLicenseNo = "LIC-002",
            ApprovalStatus = OperatorApprovalStatus.PendingApproval,
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            UpdatedAtUtc = _dateTimeProvider.UtcNow,
        });
        await dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(AccountStatus.Active);
        result.Value.ApplicationStatus.Should().Be("PendingApproval");
    }

    [Fact]
    public async Task Handle_WithConcurrentRequestsForSameAccount_SerializesAndLoserFailsWithMsg17()
    {
        var databaseName = Guid.NewGuid().ToString();
        DbContextOptions<TestDbContext> Options() => new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        long userId;
        await using (var seedContext = new TestDbContext(Options()))
        {
            var seededUser = await SeedUser(seedContext, "CurrentPass1!");
            await SeedRefreshToken(seedContext, seededUser.Id, revoked: false);
            userId = seededUser.Id;
        }

        var sharedLock = new InMemoryAccountCredentialLock();
        await using var dbContext1 = new TestDbContext(Options());
        await using var dbContext2 = new TestDbContext(Options());
        var handler1 = CreateHandler(dbContext1, sharedLock);
        var handler2 = CreateHandler(dbContext2, sharedLock);

        var task1 = handler1.Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPassA1!", userId), CancellationToken.None);
        var task2 = handler2.Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPassB1!", userId), CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        results.Count(r => r.IsSuccess).Should().Be(1,
            "two concurrent attempts with the same current password must not both succeed");
        var loser = results.Single(r => r.IsFailure);
        loser.ErrorCode.Should().Be(AuthErrorCodes.Msg17,
            "the loser re-reads the already-changed hash inside the account lock and correctly fails as wrong-current-password");

        await using var verification = new TestDbContext(Options());
        var tokens = await verification.RefreshTokens.Where(t => t.UserId == userId).ToListAsync();
        tokens.Count(t => t.RevokedAtUtc == null).Should().Be(1,
            "exactly one active session remains — the winner's; the loser never revoked it");
    }

    [Fact]
    public async Task Handle_RevokesOnlyTheSubjectsTokens_LeavingOtherUsersUntouched()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        var otherUser = await SeedUser(dbContext, "OtherPass1!", email: "other@example.com");
        await SeedRefreshToken(dbContext, user.Id, revoked: false);
        var otherToken = await SeedRefreshToken(dbContext, otherUser.Id, revoked: false);

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await dbContext.RefreshTokens.FindAsync(otherToken.Id))!.RevokedAtUtc.Should().BeNull();
    }

    private readonly InMemoryAccountCredentialLock _accountLock = new();

    private ChangePasswordCommandHandler CreateHandler(TestDbContext dbContext) =>
        CreateHandler(dbContext, _accountLock);

    private ChangePasswordCommandHandler CreateHandler(TestDbContext dbContext, IAccountCredentialLock accountLock) =>
        new(dbContext, _passwordHasher, _jwtTokenService, _dateTimeProvider, accountLock,
            NullLogger<ChangePasswordCommandHandler>.Instance);

    private async Task<User> SeedUser(
        TestDbContext dbContext,
        string password,
        string email = "user@example.com",
        UserRole role = UserRole.Traveler,
        AccountStatus status = AccountStatus.Active)
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

    private async Task<RefreshToken> SeedRefreshToken(TestDbContext dbContext, long userId, bool revoked)
    {
        var token = new RefreshToken
        {
            UserId = userId,
            TokenHash = $"hashed:seed-{Guid.NewGuid()}",
            CreatedAtUtc = _dateTimeProvider.UtcNow,
            ExpiresAtUtc = _dateTimeProvider.UtcNow.AddDays(7),
            RevokedAtUtc = revoked ? _dateTimeProvider.UtcNow : null,
        };

        dbContext.RefreshTokens.Add(token);
        await dbContext.SaveChangesAsync(CancellationToken.None);

        return token;
    }
}
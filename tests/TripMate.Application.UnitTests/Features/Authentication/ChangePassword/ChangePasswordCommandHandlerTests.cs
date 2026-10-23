using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Features.Authentication.ChangePassword;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

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
        _accountLock.AcquireCallCount.Should().Be(1,
            "the account lock must be acquired for every attempt — see the SQL Server concurrency test for the real cross-process guarantee");
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
        // The eligibility check now runs inside the single serializable transaction (lock +
        // read + verify + mutate all share it), so the transaction is attempted once — it just
        // commits as a no-op since the failure is returned before any entity is mutated.
        dbContext.TransactionExecutionCount.Should().Be(1);
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
        // Signing is attempted before any entity is mutated (still inside the one serializable
        // transaction): the transaction itself runs once, but nothing is ever written — not even
        // to the in-memory change tracker — so there is nothing to roll back.
        dbContext.TransactionExecutionCount.Should().Be(1);
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
    public async Task Handle_WhenLockAcquisitionThrows_RollsBackAndReturnsMsg127WithNoMutation()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        var oldHash = user.PasswordHash;
        await SeedRefreshToken(dbContext, user.Id, revoked: false);
        _accountLock.ThrowOnAcquire = new InvalidOperationException("Simulated applock failure.");

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        user.PasswordHash.Should().Be(oldHash);
        (await dbContext.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync())
            .Should().ContainSingle(t => t.RevokedAtUtc == null);
        // Lock acquisition is the very first step inside the transaction: nothing downstream
        // (user lookup, verification, signing) ever runs.
        _jwtTokenService.AccessTokenGenerationCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenSaveChangesFails_RetriesThenReturnsMsg127WithNoPersistedMutation()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        var oldHash = user.PasswordHash;
        await SeedRefreshToken(dbContext, user.Id, revoked: false);
        dbContext.ThrowOnSaveConcurrency = true;

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);

        // A DbUpdateConcurrencyException is retried (up to MaxAttempts) and mutates the tracked
        // C# entity on every attempt even though nothing is ever actually persisted; verify the
        // real database state with fresh, untracked reads rather than the stale local reference.
        dbContext.ClearTrackedEntities();
        var persisted = await dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        persisted.PasswordHash.Should().Be(oldHash);
        (await dbContext.RefreshTokens.AsNoTracking().Where(t => t.UserId == user.Id).ToListAsync())
            .Should().ContainSingle(t => t.RevokedAtUtc == null);
    }

    [Fact]
    public async Task Handle_WhenCommitFails_ReturnsMsg127AndDoesNotReturnCredentials()
    {
        await using var dbContext = TestDbContext.Create();
        var user = await SeedUser(dbContext, "CurrentPass1!");
        dbContext.SerializableTransactionCompletionFailureFactory =
            _ => new InvalidOperationException("Simulated commit failure.");

        var result = await CreateHandler(dbContext).Handle(
            new ChangePasswordCommand("CurrentPass1!", "NewPass2@", user.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg127);
        // The access token is generated before any mutation, inside the one transaction, so
        // signing itself cannot fail after commit. A commit failure here is a plain
        // InvalidOperationException (not DbUpdateException/deadlock), so it is never retried.
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

    // True concurrent-request serialization requires a real sp_getapplock, which the EF
    // InMemory provider used by these handler-level tests cannot execute. That guarantee is
    // proven by ChangePasswordConcurrencySqlServerTests (two separate API processes against one
    // real SQL Server), not here.

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

    private readonly FakeAccountCredentialLock _accountLock = new();

    private ChangePasswordCommandHandler CreateHandler(TestDbContext dbContext) =>
        new(dbContext, _passwordHasher, _jwtTokenService, _dateTimeProvider, _accountLock,
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
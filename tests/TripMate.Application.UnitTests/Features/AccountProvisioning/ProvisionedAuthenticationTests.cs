using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.AccountProvisioning;
using TripMate.Application.Features.Authentication.ChangePassword;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.GoogleAuth;
using TripMate.Application.Features.Authentication.Login;
using TripMate.Application.Features.Authentication.WebRefresh;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.AccountProvisioning;

public sealed class ProvisionedAuthenticationTests
{
    [Fact]
    public async Task LoginReturnsRequiredFlagOnSharedAndWebResponses()
    {
        await using var db = TestDbContext.Create();
        await Seed(db);
        var handler = new LoginCommandHandler(db, new FakePasswordHasher(), new FakeJwtTokenService(),
            new FakeDateTimeProvider(), NullLogger<LoginCommandHandler>.Instance);
        var result = await handler.Handle(new LoginCommand("provider@example.test", "TempPass1!"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.MustChangePassword.Should().BeTrue();
        WebAuthResponseDto.From(result.Value).MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public async Task GoogleSignInPreservesRequiredPasswordChangeInWebProjection()
    {
        await using var db = TestDbContext.Create();
        await Seed(db);
        var firebase = new Mock<IFirebaseAuthService>();
        firebase.Setup(service => service.VerifyIdTokenAsync("verified-google", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FirebaseTokenValidationResult("provider-google", "provider@example.test", true, SignInProvider: "google.com"));
        var handler = new GoogleAuthCommandHandler(db, firebase.Object, new FakeJwtTokenService(),
            new FakeDateTimeProvider(), NullLogger<GoogleAuthCommandHandler>.Instance);
        var result = await handler.Handle(new GoogleAuthCommand("verified-google"), default);
        result.IsSuccess.Should().BeTrue();
        WebGoogleAuthResponseDto.From(result.Value).MustChangePassword.Should().BeTrue();
        (await db.Users.SingleAsync()).MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshPreservesRequiredPasswordChangeSoTheChangePageCanBeRestored()
    {
        await using var db = TestDbContext.Create();
        var user = await Seed(db);
        user.MustChangePassword = false;
        var jwt = new FakeJwtTokenService();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = jwt.HashRefreshToken("session"),
            CreatedAtUtc = new FakeDateTimeProvider().UtcNow,
            ExpiresAtUtc = new FakeDateTimeProvider().UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();
        user.MustChangePassword = true;
        await db.SaveChangesAsync();
        var result = await new WebRefreshCommandHandler(db, jwt, new FakeDateTimeProvider())
            .Handle(new WebRefreshCommand("session"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.MustChangePassword.Should().BeTrue();
        jwt.AccessTokenGenerationCount.Should().Be(1);
    }

    [Theory]
    [InlineData("TempPass1!", false)]
    [InlineData("WrongPass1!", true)]
    public async Task OnlySuccessfulPasswordChangeClearsPersistedFlag(string currentPassword, bool expectedFlag)
    {
        await using var db = TestDbContext.Create();
        var user = await Seed(db);
        var result = await new ChangePasswordCommandHandler(db, new FakePasswordHasher(), new FakeJwtTokenService(),
            new FakeDateTimeProvider(), new FakeAccountCredentialLock(), NullLogger<ChangePasswordCommandHandler>.Instance)
            .Handle(new ChangePasswordCommand(currentPassword, "NewPass2!", user.Id), default);
        (await db.Users.SingleAsync()).MustChangePassword.Should().Be(expectedFlag);
        if (result.IsSuccess) result.Value.MustChangePassword.Should().BeFalse();
    }

    private static async Task<User> Seed(TestDbContext db)
    {
        var user = new User
        {
            Email = "provider@example.test",
            FullName = "Provider",
            Role = UserRole.ServiceProvider,
            Status = AccountStatus.Active,
            MustChangePassword = true,
            PasswordHash = new FakePasswordHasher().Hash("TempPass1!")
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
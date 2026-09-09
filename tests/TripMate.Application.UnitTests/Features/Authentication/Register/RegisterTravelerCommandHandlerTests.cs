using FluentAssertions;
using Moq;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.Register;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.Register;

public class RegisterTravelerCommandHandlerTests
{
    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly Mock<IFirebaseAuthService> _firebaseAuthService = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly Mock<IDateTimeProvider> _dateTimeProvider = new();
    private readonly RegisterTravelerCommandHandler _handler;

    public RegisterTravelerCommandHandlerTests()
    {
        _dateTimeProvider.Setup(p => p.UtcNow).Returns(DateTimeOffset.UtcNow);

        _firebaseAuthService
            .Setup(s => s.VerifyIdTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string token, CancellationToken ct) => new FirebaseTokenValidationResult(
                "uid-123",
                token == "existing-token" ? "existing@example.com" : "newuser@example.com",
                false));

        _handler = new RegisterTravelerCommandHandler(
            _dbContext,
            _firebaseAuthService.Object,
            _passwordHasher,
            _dateTimeProvider.Object);
    }

    [Fact]
    public async Task Handle_WithValidCommand_CreatesPendingUserAndHashesPassword()
    {
        var command = new RegisterTravelerCommand(
            "newuser@example.com",
            "Password123!",
            "Nguyen Van A",
            "0912345678",
            true,
            "valid-token");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(AccountStatus.PendingEmailVerification.ToString());
        result.Value.Email.Should().Be("newuser@example.com");
        result.Value.MessageCode.Should().Be(AuthErrorCodes.Msg07);

        var savedUser = _dbContext.Users.Single(u => u.Email == "newuser@example.com");
        savedUser.PasswordHash.Should().Be("hashed:Password123!");
    }

    [Fact]
    public async Task Handle_WithDuplicateEmail_ReturnsMsg03()
    {
        _dbContext.Users.Add(new User
        {
            Email = "existing@example.com",
            FullName = "Existing User",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active
        });
        await _dbContext.SaveChangesAsync();

        var command = new RegisterTravelerCommand(
            "existing@example.com",
            "Password123!",
            "Nguyen Van A",
            null,
            true,
            "existing-token");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(AuthErrorCodes.Msg03);
    }

    [Fact]
    public async Task Handle_TrimsAndNormalizesEmailAndFullName()
    {
        var command = new RegisterTravelerCommand(
            "  NEWUSER@EXAMPLE.COM  ",
            "Password123!",
            "  Nguyen Van A  ",
            null,
            true,
            "valid-token");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var savedUser = _dbContext.Users.Single(u => u.Email == "newuser@example.com");
        savedUser.FullName.Should().Be("Nguyen Van A");
    }
}
